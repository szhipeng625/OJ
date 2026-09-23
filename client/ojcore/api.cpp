// api.cpp — 导出接口实现
#include "ojcore.h"
#include "judge.h"
#include "mysql_dao.h"

#include "resp_client.h"

#include <windows.h>

#include <algorithm>
#include <exception>
#include <fstream>
#include <map>
#include <memory>
#include <sstream>
#include <utility>

#include <cstring>
#include <ctime>
#include <sys/stat.h>
namespace {

std::string g_problemDir;
std::string g_dataDir;
std::string g_tempDir;
std::string g_lastError;                 // 最近一次 native 错误信息（供排查）
oj::RespClient g_redis;                 // 远端 LSM 存储客户端（RESP，6379）
long long   g_submitSeq = 0;            // 远端不可用时的本地兜底序号

std::string readFile(const std::string& path) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return "";
    std::stringstream ss; ss << f.rdbuf();
    return ss.str();
}

void writeFile(const std::string& path, const std::string& content) {
    std::ofstream f(path, std::ios::binary | std::ios::trunc);
    f << content;
}

bool exists(const std::string& path) {
    return GetFileAttributesA(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

std::string jsonEscape(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 8);
    for (char c : s) {
        switch (c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n";  break;
            case '\r': out += "\\r";  break;
            case '\t': out += "\\t";  break;
            default:
                if ((unsigned char)c < 0x20) { char buf[8]; snprintf(buf, 8, "\\u%04x", c); out += buf; }
                else out += c;
        }
    }
    return out;
}

// 把 C 字符串复制成可由调用方释放的 char*
const char* dup(const std::string& s) {
    char* p = (char*)malloc(s.size() + 1);
    memcpy(p, s.c_str(), s.size() + 1);
    return p;
}

// 把 g_problemDir 的最后一级去掉，得到 server root（contests 与 problems 同级）
std::string serverRoot() {
    size_t p = g_problemDir.find_last_of("\\/");
    return (p == std::string::npos) ? g_problemDir : g_problemDir.substr(0, p);
}

// 解析 "YYYY-MM-DD HH:MM:SS" 为 time_t（本地时区）。失败返回 -1。
time_t parseTime(const std::string& s) {
    int y, mo, d, h, mi, se;
    if (sscanf(s.c_str(), "%d-%d-%d %d:%d:%d", &y, &mo, &d, &h, &mi, &se) != 6) return -1;
    struct tm tmv = {};
    tmv.tm_year = y - 1900; tmv.tm_mon = mo - 1; tmv.tm_mday = d;
    tmv.tm_hour = h; tmv.tm_min = mi; tmv.tm_sec = se;
    return mktime(&tmv);
}

// 从 JSON 原文里按 key 提取字符串值（引号内容）
std::string jsonStr(const std::string& j, const char* key) {
    std::string k = std::string("\"") + key + "\"";
    size_t p = j.find(k);
    if (p == std::string::npos) return "";
    p = j.find(':', p + k.size());
    if (p == std::string::npos) return "";
    p++;
    while (p < j.size() && (j[p]==' '||j[p]=='\t'||j[p]=='\n'||j[p]=='\r')) p++;
    if (p >= j.size() || j[p] != '"') return "";
    size_t e = p + 1;
    while (e < j.size() && j[e] != '"') {
        if (j[e] == '\\') e++;
        e++;
    }
    return j.substr(p + 1, e - p - 1);
}

// 从 JSON 原文里按 key 提取整数
long long jsonInt(const std::string& j, const char* key, long long def = 0) {
    std::string k = std::string("\"") + key + "\"";
    size_t p = j.find(k);
    if (p == std::string::npos) return def;
    p = j.find(':', p + k.size());
    if (p == std::string::npos) return def;
    return atoll(j.c_str() + p + 1);
}

// 反转义 JSON 字符串（jsonEscape 的逆操作）
std::string jsonUnescape(const std::string& s) {
    std::string out;
    out.reserve(s.size());
    for (size_t i = 0; i < s.size(); ++i) {
        if (s[i] == '\\' && i + 1 < s.size()) {
            char c = s[++i];
            switch (c) {
                case '"':  out += '"';  break;
                case '\\': out += '\\'; break;
                case 'n':  out += '\n'; break;
                case 'r':  out += '\r'; break;
                case 't':  out += '\t'; break;
                default:   out += '\\'; out += c; break;
            }
        } else out += s[i];
    }
    return out;
}

// 解析 JSON 数组 [1,2,3] 成 vector<int>
std::vector<int> parseIntArray(const std::string& j, const char* key) {
    std::vector<int> res;
    std::string k = std::string("\"") + key + "\"";
    size_t p = j.find(k);
    if (p == std::string::npos) return res;
    p = j.find('[', p + k.size());
    if (p == std::string::npos) return res;
    size_t e = j.find(']', p);
    if (e == std::string::npos) return res;
    std::string arr = j.substr(p + 1, e - p - 1);
    char* start = (char*)arr.c_str();
    char* tok = strtok(start, ", ");
    while (tok) { res.push_back(atoi(tok)); tok = strtok(nullptr, ", "); }
    return res;
}

// 题目元数据（meta.json，缺省有默认值）
struct ProblemMeta {
    long long timeLimitMs = 1000;
    long long memLimitMB = 256;
    std::string tagsJson;     // JSON 数组原文（如 ["基础","模拟"]），空 = []
    std::string updatedAt;
};

// 简单解析 meta.json（不引第三方库）：按 key 提取标量或 tags 数组原文
ProblemMeta readMeta(const std::string& dir) {
    ProblemMeta m;
    std::string s = readFile(dir + "\\meta.json");
    if (s.empty()) return m;
    auto findVal = [&](const char* key, std::string& out) {
        std::string k = std::string("\"") + key + "\"";
        size_t p = s.find(k);
        if (p == std::string::npos) return;
        p = s.find(':', p + k.size());
        if (p == std::string::npos) return;
        ++p;
        while (p < s.size() && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) ++p;
        size_t e = p;
        if (p < s.size() && s[p] == '"') {
            e = s.find('"', p + 1);
            if (e == std::string::npos) return;
            out = s.substr(p + 1, e - p - 1);
        } else {
            while (e < s.size() && s[e] != ',' && s[e] != '}' && s[e] != '\n' && s[e] != '\r') ++e;
            out = s.substr(p, e - p);
        }
    };
    std::string v;
    if (findVal("timeLimitMs", v), !v.empty()) m.timeLimitMs = atoll(v.c_str());
    if (findVal("memLimitMB", v), !v.empty()) m.memLimitMB = atoll(v.c_str());
    findVal("updatedAt", m.updatedAt);
    // tags 数组原文
    size_t tp = s.find("\"tags\"");
    if (tp != std::string::npos) {
        tp = s.find('[', tp);
        if (tp != std::string::npos) {
            size_t te = s.find(']', tp);
            if (te != std::string::npos) m.tagsJson = s.substr(tp, te - tp + 1);
        }
    }
    return m;
}

struct ProblemInfo {
    int id; std::string title, desc, sampleIn, sampleOut;
    long long timeLimitMs, memLimitMB;
    std::string tagsJson;
    std::string samplesJson;
};
std::vector<ProblemInfo> scanProblems() {
    std::vector<ProblemInfo> res;
    std::string pattern = g_problemDir + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return res;
    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        std::string name = fd.cFileName;
        if (name == "." || name == "..") continue;
        int id = atoi(name.c_str());
        if (id <= 0) continue;
        ProblemInfo pi; pi.id = id;
        std::string dir = g_problemDir + "\\" + name;
        std::string st = readFile(dir + "\\statement.txt");
        // 第一行作标题，其余作描述
        size_t nl = st.find('\n');
        if (nl == std::string::npos) { pi.title = st; pi.desc = ""; }
        else { pi.title = st.substr(0, nl); pi.desc = st.substr(nl + 1); }
        pi.sampleIn  = readFile(dir + "\\sample.in");
        pi.sampleOut = readFile(dir + "\\sample.out");
        // 测试样例（丰富题面）：testcases.json 里的 cases 数组原文
        {
            std::string tc = readFile(dir + "\\testcases.json");
            size_t sp = tc.find("\"cases\"");
            if (sp != std::string::npos) {
                sp = tc.find('[', sp);
                if (sp != std::string::npos) {
                    size_t ep = tc.find(']', sp);
                    if (ep != std::string::npos) pi.samplesJson = tc.substr(sp, ep - sp + 1);
                }
            }
        }
        auto meta = readMeta(dir);
        pi.timeLimitMs = meta.timeLimitMs;
        pi.memLimitMB = meta.memLimitMB;
        pi.tagsJson = meta.tagsJson;
        res.push_back(pi);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    return res;
}

// ---- native 异常防护 ----
// 任何 native 异常（C++ 异常 / 访问违例）都转成错误码返回，
// 绝不穿过 P/Invoke 边界导致宿主进程崩溃。
static int redis_connect_cpp(const std::string& host, int port, std::string& errOut) {
    try {
        return g_redis.connect(host, port) ? 0 : -1;
    } catch (const std::exception& e) {
        errOut = e.what();
        return -1;
    } catch (...) {
        errOut = "unknown C++ exception during redis connect";
        return -1;
    }
}

static int init_redis_safe(const std::string& host, int port, std::string& errOut) {
    __try {
        return redis_connect_cpp(host, port, errOut);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        errOut = "native access violation during redis connect";
        return -2;
    }
}

// 判题核心：跑题、构造结果 JSON、落 LSM。virtual_ 非 0 表示虚拟参赛。
static const char* do_judge(int problem_id, const char* code,
                            const char* username, int virtual_, int contest_id) {
    std::string problemDir = g_problemDir + "\\" + std::to_string(problem_id);
    std::string src  = g_tempDir + "\\user.cpp";
    std::string exe  = g_tempDir + "\\user.exe";
    std::string out  = g_tempDir + "\\__out.tmp";
    writeFile(src, code ? code : "");

    // 判题前确保测试数据已生成（按需生成，耗时不计入判题计时）
    if (oj::mysql_available()) {
        std::string derr;
        oj::ensure_problem_data(problem_id, g_problemDir, derr);
    }

    std::string detail, verdict;
    std::vector<oj::CaseResult> cases;

    std::string compileErr;
    oj::JudgeOptions jopt;
    // 从题目 meta.json 读取时间/内存限制（默认 1000ms / 256MB）
    auto meta = readMeta(problemDir);
    jopt.timeoutMs = (DWORD)meta.timeLimitMs;
    jopt.memBytes = (SIZE_T)(meta.memLimitMB) * 1024 * 1024;
    if (!oj::compile_cpp(src, exe, compileErr)) {
        verdict = "CE";
        detail = compileErr.empty() ? "编译失败" : compileErr;
    } else {
        // 判题数据来源：题目目录下各数据生成器子目录（二级目录）的 *.in/*.out
        cases = oj::run_tests(exe, problemDir, out, jopt);
        verdict = oj::summarize(cases);
        int acn = 0; for (auto& c : cases) if (c.passed) acn++;
        detail = "通过 " + std::to_string(acn) + "/" + std::to_string(cases.size()) + " 组测试点";
        // 未通过时，返回第一个失败测试点所属生成器的描述文本
        if (verdict != "AC") {
            for (auto& c : cases) {
                if (!c.passed && !c.info.empty()) { detail = c.info; break; }
            }
        }
        DeleteFileA(exe.c_str());
    }

    // 提交时间戳
    SYSTEMTIME st;
    GetLocalTime(&st);
    char tsBuf[32];
    snprintf(tsBuf, sizeof(tsBuf), "%04d-%02d-%02d %02d:%02d:%02d",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);

    std::string uname = (username && *username) ? username : "anonymous";

    // 比赛提交先落 MySQL，用其 AUTO_INCREMENT id 作为本次提交 sid，
    // 保证「提交列表（MySQL id）」与「双击详情（LSM submission:{id}）」键一一对应。
    // 远端 LSM 的 INCR 目前不可靠（返回非递增），仅作 MySQL 不可用时的兜底。
    long long sid = 0;
    try {
        if (contest_id > 0 && oj::mysql_available()) {
            long long uid = 0;
            if (!oj::mysql_user_id_by_name(uname, uid)) {
                if (!oj::mysql_user_id_by_name("anonymous", uid)) uid = 0;
            }
            if (uid > 0) {
                int totalMs = 0;
                for (auto& c : cases) totalMs += (int)c.timeMs;
                sid = oj::mysql_upsert_submission(uid, problem_id, contest_id, verdict, detail,
                                                  totalMs, virtual_ != 0, tsBuf);
            }
        }
    } catch (...) { /* MySQL 写入失败仅记录，判题结果照常返回 */ }

    if (sid <= 0) {
        sid = (contest_id > 0) ? g_redis.incr("meta:seq") : ++g_submitSeq;
        if (sid <= 0) sid = ++g_submitSeq;
    }

    // 拼结果 JSON（含 problemId / username / virtual / ts 供按题查询历史提交与榜单聚合）
    std::string json = "{\"id\":" + std::to_string(sid)
        + ",\"problemId\":" + std::to_string(problem_id)
        + ",\"contestId\":" + std::to_string(contest_id)
        + ",\"username\":\"" + jsonEscape(uname) + "\""
        + ",\"virtual\":" + (virtual_ ? "true" : "false")
        + ",\"verdict\":\"" + verdict + "\""
        + ",\"detail\":\"" + jsonEscape(detail) + "\""
        + ",\"ts\":\"" + tsBuf + "\""
        + ",\"code\":\"" + jsonEscape(code ? code : "") + "\""
        + ",\"cases\":[";
    for (size_t i = 0; i < cases.size(); ++i) {
        auto& c = cases[i];
        if (i) json += ",";
        json += "{\"name\":\"" + jsonEscape(c.name) + "\""
             + ",\"timeMs\":" + std::to_string(c.timeMs)
             + ",\"passed\":" + (c.passed ? "true" : "false")
             + ",\"info\":\"" + jsonEscape(c.info) + "\"}";
    }
    json += "]}";

    // LSM 只持久化「比赛提交」（赛时代码）；练习提交不落 LSM。失败不影响判题结果返回。
    if (contest_id > 0) {
        try {
            std::string sidKey = std::to_string(sid);
            // 完整记录（含代码与测试点），供双击查看详情
            g_redis.set("submission:" + sidKey, json);
            // 便于按 (用户, 比赛, 题目) 直接取最近一次提交
            g_redis.set("latest:" + uname + ":" + std::to_string(contest_id) + ":" + std::to_string(problem_id), sidKey);
            // 列表用摘要（不含 code/cases，轻量），同时保留完整历史用于榜单罚时统计
            std::string summary = "{\"id\":" + sidKey
                + ",\"problemId\":" + std::to_string(problem_id)
                + ",\"contestId\":" + std::to_string(contest_id)
                + ",\"username\":\"" + jsonEscape(uname) + "\""
                + ",\"virtual\":" + (virtual_ ? "true" : "false")
                + ",\"verdict\":\"" + verdict + "\""
                + ",\"detail\":\"" + jsonEscape(detail) + "\""
                + ",\"ts\":\"" + tsBuf + "\"}";
            g_redis.hset("contest_subs:" + std::to_string(contest_id), sidKey, summary);
            // 用户在本场每题最近一次判定（哈希：field=pid, value=verdict）
            g_redis.hset("progress:" + uname + ":" + std::to_string(contest_id),
                         std::to_string(problem_id), verdict);
        } catch (...) { /* 存储失败仅记录，判题结果照常返回 */ }
    }

    return dup(json);
}

// 按 (username, problem_id, contest_id) 取最近一次提交（id 最大）。无则返回空串。
std::string latestSubmission(const std::string& uname, int problem_id, int contest_id) {
    try {
        auto sid = g_redis.get("latest:" + uname + ":" + std::to_string(contest_id) + ":" + std::to_string(problem_id));
        if (sid && !sid->empty()) {
            auto v = g_redis.get("submission:" + *sid);
            if (v) return *v;
        }
    } catch (...) { /* 存储不可用返回空 */ }
    return "";
}

// 某用户在某比赛（0=练习）下每题最近一次提交结果，输出 JSON 数组
std::string userProgressJson(const std::string& uname, int contest_id) {
    std::map<long long, std::string> verdicts;   // pid -> verdict（按 pid 排序）
    try {
        std::string hkey = "progress:" + uname + ":" + std::to_string(contest_id);
        auto pids = g_redis.hkeys(hkey);
        for (auto& p : pids) {
            long long pid = atoll(p.c_str());
            if (pid <= 0) continue;
            auto v = g_redis.hget(hkey, p);
            if (v) verdicts[pid] = *v;
        }
    } catch (...) { /* 存储不可用返回空 */ }
    std::string json = "[";
    bool first = true;
    for (auto& kv : verdicts) {
        if (!first) json += ",";
        first = false;
        bool ac = (kv.second == "AC");
        json += "{\"problemId\":" + std::to_string(kv.first)
             + ",\"verdict\":\"" + jsonEscape(kv.second) + "\""
             + ",\"ac\":" + (ac ? "true" : "false") + "}";
    }
    json += "]";
    return json;
}

} // namespace

// 报名哈希 key（放在 extern "C" 外，避免 C 链接返回 C++ 类型的告警）
static std::string regKey(int cid) {
    return "reg:" + std::to_string(cid);
}

// 从远端 LSM 聚合榜单：保留完整提交历史，能正确统计「AC 前的错误提交」罚时；
// 虚拟参赛者以报名时间为计时基线。
static std::string boardFromLsm(int cid, time_t startT) {
    struct Cell { int wrong = 0; long long acMin = -1; };   // acMin<0 表示未 AC
    struct UserStat {
        std::string username;
        std::map<int, Cell> cells;
    };
    std::map<std::string, UserStat> offUsers, virtUsers;

    // 虚拟参赛者：从报名时间开始计时
    std::map<std::string, time_t> virtRegTs;
    try {
        std::string rhkey = "reg:" + std::to_string(cid);
        auto unames = g_redis.hkeys(rhkey);
        for (auto& u : unames) {
            auto v = g_redis.hget(rhkey, u);
            if (!v) continue;
            const std::string& val = *v;
            if (val.find("\"virtual\":true") == std::string::npos) continue;
            time_t t = parseTime(jsonStr(val, "ts"));
            if (t > 0) virtRegTs[u] = t;
        }
    } catch (...) {}

    auto consider = [&](std::map<std::string, UserStat>& table,
                        const std::string& uname, int pid,
                        bool ac, const std::string& ts, time_t baseline) {
        if (table.find(uname) == table.end()) table[uname].username = uname;
        Cell& c = table[uname].cells[pid];
        if (c.acMin >= 0) return;          // 已 AC 不再计分
        if (ac) {
            time_t t = parseTime(ts);
            c.acMin = (baseline > 0 && t > 0) ? (long long)difftime(t, baseline) / 60 : 0;
        } else {
            c.wrong++;
        }
    };

    // 收集本场比赛全部提交，按提交 id（时间顺序）排序，保证「AC 之前的错误提交」统计正确
    struct Sub { long long id; std::string uname; int pid; bool ac; bool virt; std::string ts; time_t baseline; };
    std::vector<Sub> subs;
    try {
        std::string shkey = "contest_subs:" + std::to_string(cid);
        auto sids = g_redis.hkeys(shkey);
        for (auto& sk : sids) {
            auto sv = g_redis.hget(shkey, sk);
            if (!sv) continue;
            const std::string& val = *sv;
            long long pid = jsonInt(val, "problemId");
            std::string verdict = jsonStr(val, "verdict");
            bool ac = (verdict == "AC");
            std::string uname = jsonStr(val, "username");
            if (uname.empty()) uname = "anonymous";
            std::string ts = jsonStr(val, "ts");
            bool virt = val.find("\"virtual\":true") != std::string::npos;
            time_t baseline = startT;
            if (virt) {
                auto ri = virtRegTs.find(uname);
                if (ri != virtRegTs.end()) baseline = ri->second;
            }
            Sub s;
            s.id = jsonInt(val, "id");
            s.uname = uname;
            s.pid = (int)pid;
            s.ac = ac;
            s.virt = virt;
            s.ts = ts;
            s.baseline = baseline;
            subs.push_back(std::move(s));
        }
    } catch (...) {}
    std::sort(subs.begin(), subs.end(), [](const Sub& a, const Sub& b) { return a.id < b.id; });
    for (auto& s : subs) {
        consider(s.virt ? virtUsers : offUsers, s.uname, s.pid, s.ac, s.ts, s.baseline);
    }

    // 聚合输出
    auto emitTable = [&](const std::map<std::string, UserStat>& table) -> std::string {
        struct Row { std::string name; int solved; long long penalty; };
        std::vector<Row> rows;
        for (auto& kv : table) {
            Row r; r.name = kv.first; r.solved = 0; r.penalty = 0;
            for (auto& cell : kv.second.cells) {
                if (cell.second.acMin >= 0) {
                    r.solved++;
                    r.penalty += cell.second.acMin + (long long)cell.second.wrong * 20;
                }
            }
            if (r.solved > 0 || !kv.second.cells.empty()) rows.push_back(r);
        }
        std::sort(rows.begin(), rows.end(), [](const Row& a, const Row& b) {
            if (a.solved != b.solved) return a.solved > b.solved;
            return a.penalty < b.penalty;
        });
        std::string out = "[";
        for (size_t i = 0; i < rows.size(); ++i) {
            if (i) out += ",";
            out += "{\"rank\":" + std::to_string(i + 1)
                 + ",\"username\":\"" + jsonEscape(rows[i].name) + "\""
                 + ",\"solved\":" + std::to_string(rows[i].solved)
                 + ",\"penalty\":" + std::to_string(rows[i].penalty) + "}";
        }
        out += "]";
        return out;
    };

    return "{\"official\":" + emitTable(offUsers)
         + ",\"virtual\":"  + emitTable(virtUsers) + "}";
}

// 每题最近一次判定结果：MySQL 优先（权威），比赛时回退远端 LSM
static std::string progressJson(const std::string& uname, int contest_id) {
    std::string out;
    if (oj::mysql_available() && oj::mysql_user_progress(uname, contest_id, out))
        return out;
    return userProgressJson(uname, contest_id);
}

extern "C" {

OJ_API int oj_init(const char* problem_dir, const char* data_dir) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    g_problemDir = problem_dir ? problem_dir : "";
    g_dataDir = data_dir ? data_dir : "";
    g_tempDir = g_dataDir + "\\temp";
    CreateDirectoryA(g_dataDir.c_str(), NULL);
    CreateDirectoryA(g_tempDir.c_str(), NULL);
    // 本地模式：仅设置目录，提交记录持久化由 oj_init_redis 连接远端 LSM（RESP）完成
    g_submitSeq = 0;
    return 0;
}

// 连接远端 LSM 存储服务（RESP，默认端口 6379）。返回 0 成功，非 0 失败。
OJ_API int oj_init_redis(const char* host, int port) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    g_lastError.clear();
    return init_redis_safe(host ? host : "127.0.0.1", port > 0 ? port : 6379, g_lastError);
}

OJ_API const char* oj_get_problems(void) {
    auto probs = scanProblems();
    std::string json = "[";
    for (size_t i = 0; i < probs.size(); ++i) {
        auto& p = probs[i];
        if (i) json += ",";
        json += "{\"id\":" + std::to_string(p.id)
             + ",\"title\":\"" + jsonEscape(p.title) + "\""
             + ",\"description\":\"" + jsonEscape(p.desc) + "\""
             + ",\"sampleIn\":\"" + jsonEscape(p.sampleIn) + "\""
             + ",\"sampleOut\":\"" + jsonEscape(p.sampleOut) + "\""
             + ",\"timeLimitMs\":" + std::to_string(p.timeLimitMs)
             + ",\"memLimitMB\":" + std::to_string(p.memLimitMB)
             + ",\"tags\":" + (p.tagsJson.empty() ? "[]" : p.tagsJson)
             + ",\"samples\":" + (p.samplesJson.empty() ? "[]" : p.samplesJson)
             + "}";
    }
    json += "]";
    return dup(json);
}

OJ_API const char* oj_get_submissions(int problem_id) {
    std::string json = "[";
    bool first = true;
    try {
        std::string hkey = "problem_subs:" + std::to_string(problem_id);
        auto sids = g_redis.hkeys(hkey);
        std::vector<long long> ids;
        for (auto& s : sids) ids.push_back(atoll(s.c_str()));
        std::sort(ids.begin(), ids.end());
        for (long long sid : ids) {
            auto v = g_redis.hget(hkey, std::to_string(sid));
            if (!v) continue;
            if (!first) json += ",";
            first = false;
            json += *v;
        }
    } catch (...) { /* 存储不可用返回空 */ }
    json += "]";
    return dup(json);
}

// 获取某用户在某题（contest_id=0 为练习）下的最近一次提交（含 code）。
// 无记录返回 {"found":false}；有则 {"found":true,"id":..,"verdict":"..","code":"..",...}
OJ_API const char* oj_get_user_solution(int problem_id, const char* username, int contest_id) {
    std::string uname = (username && *username) ? username : "anonymous";
    std::string val = latestSubmission(uname, problem_id, contest_id);
    if (val.empty()) return dup("{\"found\":false}");
    return dup("{\"found\":true," + val.substr(1));
}

// 某用户练习模式（contest_id=0）每题最近一次提交结果，用于题库通过/未通过标记
OJ_API const char* oj_get_user_progress(const char* username) {
    std::string uname = (username && *username) ? username : "anonymous";
    return dup(progressJson(uname, 0));
}

// 某用户在某场比赛下每题最近一次提交结果，用于比赛题目列表通过/未通过标记
OJ_API const char* oj_get_user_contest_progress(int cid, const char* username) {
    std::string uname = (username && *username) ? username : "anonymous";
    return dup(progressJson(uname, cid));
}

OJ_API const char* oj_submit(int problem_id, const char* code) {
    return do_judge(problem_id, code, "anonymous", 0, 0);
}

OJ_API const char* oj_submit_ex(int problem_id, const char* code,
                                const char* username, int virtual_) {
    return do_judge(problem_id, code,
                    (username && *username) ? username : "anonymous",
                    virtual_ ? 1 : 0, 0);
}

// 比赛提交：contest_id 写入提交记录（LSM + MySQL），供榜单与提交记录按比赛聚合
OJ_API const char* oj_submit_contest(int problem_id, const char* code,
                                     const char* username, int virtual_,
                                     int contest_id) {
    return do_judge(problem_id, code,
                    (username && *username) ? username : "anonymous",
                    virtual_ ? 1 : 0, contest_id);
}

// 读取某次比赛提交的完整详情（含代码与测试点）
OJ_API const char* oj_get_submission_detail(long long sid) {
    try {
        auto v = g_redis.get("submission:" + std::to_string(sid));
        if (v && !v->empty()) return dup(*v);
    } catch (...) {}
    return dup("{\"found\":false}");
}

// 判题核心：跑题、构造结果 JSON、落 LSM。virtual_ 非 0 表示虚拟参赛。
// ===== 比赛 =====

OJ_API const char* oj_get_contests(void) {
    std::string root = serverRoot();
    std::string croot = root + "\\contests";
    std::vector<int> ids;
    std::string pattern = croot + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
            std::string name = fd.cFileName;
            if (name == "." || name == "..") continue;
            int cid = atoi(name.c_str());
            if (cid > 0) ids.push_back(cid);
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    std::sort(ids.begin(), ids.end());

    std::string json = "[";
    bool first = true;
    for (int cid : ids) {
        std::string raw = readFile(croot + "\\" + std::to_string(cid) + "\\contest.json");
        std::string name = jsonStr(raw, "name");
        std::string st = jsonStr(raw, "startTime");
        std::string et = jsonStr(raw, "endTime");
        int pc = (int)parseIntArray(raw, "problems").size();
        if (!first) json += ",";
        first = false;
        json += "{\"id\":" + std::to_string(cid)
             + ",\"name\":\"" + jsonEscape(name) + "\""
             + ",\"problemCount\":" + std::to_string(pc)
             + ",\"startTime\":\"" + jsonEscape(st) + "\""
             + ",\"endTime\":\"" + jsonEscape(et) + "\"}";
    }
    json += "]";
    return dup(json);
}

OJ_API const char* oj_get_contest(int cid) {
    std::string root = serverRoot();
    std::string path = root + "\\contests\\" + std::to_string(cid) + "\\contest.json";
    std::string raw = readFile(path);
    if (raw.empty() || raw[0] != '{') {
        return dup("{\"ok\":false,\"error\":\"比赛不存在\"}");
    }
    raw = "{\"ok\":true," + raw.substr(1);
    return dup(raw);
}

OJ_API const char* oj_get_board(int cid) {
    // 1. 读比赛配置
    std::string root = serverRoot();
    std::string cpath = root + "\\contests\\" + std::to_string(cid) + "\\contest.json";
    std::string craw = readFile(cpath);
    if (craw.empty()) return dup("{\"official\":[],\"virtual\":[]}");
    std::vector<int> pids = parseIntArray(craw, "problems");
    std::string startStr = jsonStr(craw, "startTime");
    time_t startT = parseTime(startStr);

    // MySQL 计算罚时（submissions.wrong_count + 最早 AC 时间），LSM 兜底
    if (oj::mysql_available()) {
        std::string csv;
        for (size_t i = 0; i < pids.size(); ++i) {
            if (i) csv += ",";
            csv += std::to_string(pids[i]);
        }
        if (csv.empty()) csv = "0";
        std::string off, virt;
        if (oj::mysql_board(cid, startStr, csv, off, virt))
            return dup("{\"official\":" + off + ",\"virtual\":" + virt + "}");
    }

    // LSM 兜底：从完整提交历史现算错误次数与最早 AC 时间
    if (g_redis.is_connected())
        return dup(boardFromLsm(cid, startT));

    return dup("{\"official\":[],\"virtual\":[]}");
}

// ===== 比赛报名 / 比赛提交记录 =====

// 报名（幂等）。MySQL 可用时写 contest_registrations；远端 LSM 始终写一份本地凭证。
OJ_API const char* oj_contest_register(int cid, const char* username, int virtual_) {
    std::string uname = (username && *username) ? username : "anonymous";
    SYSTEMTIME st; GetLocalTime(&st);
    char tbuf[32];
    snprintf(tbuf, sizeof(tbuf), "%04d-%02d-%02d %02d:%02d:%02d",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    bool virt = virtual_ != 0;
    bool ok = true;
    try {
        if (oj::mysql_available()) {
            long long uid = 0; std::string err;
            if (oj::mysql_user_id_by_name(uname, uid))
                ok = oj::mysql_contest_register(uid, cid, virt, err);
        }
    } catch (...) { ok = false; }
    try {
        std::string val = "{\"username\":\"" + jsonEscape(uname) + "\""
            + ",\"virtual\":" + (virt ? "true" : "false")
            + ",\"ts\":\"" + tbuf + "\"}";
        g_redis.hset(regKey(cid), uname, val);
        ok = true;   // 远端 LSM 写入成功即视为报名成功
    } catch (...) {}
    return dup(std::string("{\"ok\":") + (ok ? "true" : "false")
             + ",\"registered\":true,\"virtual\":" + (virt ? "true" : "false") + "}");
}

// 查询当前用户是否已报名及参赛类型
OJ_API const char* oj_contest_registration(int cid, const char* username) {
    std::string uname = (username && *username) ? username : "anonymous";
    bool registered = false, virt = false;
    try {
        if (oj::mysql_available()) {
            long long uid = 0; bool r = false, v = false;
            if (oj::mysql_user_id_by_name(uname, uid) &&
                oj::mysql_contest_registration(uid, cid, r, v)) {
                registered = r; virt = v;
            }
        }
    } catch (...) {}
    if (!registered) {
        try {
            auto vv = g_redis.hget(regKey(cid), uname);
            if (vv && !vv->empty()) {
                registered = true;
                virt = vv->find("\"virtual\":true") != std::string::npos;
            }
        } catch (...) {}
    }
    return dup(std::string("{\"ok\":true,\"registered\":") + (registered ? "true" : "false")
             + ",\"virtual\":" + (virt ? "true" : "false") + "}");
}

// 比赛提交记录：LSM（保留完整历史）优先，时间倒序；MySQL 兜底（每人每题最后一发）
// view_all=0 时只返回 username 本人记录（比赛进行中参赛者互不可见）
OJ_API const char* oj_contest_submissions(int cid, const char* username, int view_all) {
    std::string uname = (username && *username) ? username : "anonymous";

    // LSM 保留本场全部提交历史，优先返回
    if (g_redis.is_connected()) {
        struct Rec { long long id; std::string ts; std::string val; };
        std::vector<Rec> recs;
        bool any = false;
        try {
            std::string shkey = "contest_subs:" + std::to_string(cid);
            auto sids = g_redis.hkeys(shkey);
            for (auto& sk : sids) {
                auto sv = g_redis.hget(shkey, sk);
                if (!sv) continue;
                any = true;
                const std::string& val = *sv;
                if (!view_all) {
                    std::string recUser = jsonStr(val, "username");
                    if (recUser != uname) continue;
                }
                Rec r;
                r.id = jsonInt(val, "id");
                r.ts = jsonStr(val, "ts");
                r.val = val;
                recs.push_back(std::move(r));
            }
        } catch (...) {}
        if (any) {   // 本场确有提交，直接返回（即使 view_all=0 过滤后可能为空）
            std::sort(recs.begin(), recs.end(), [](const Rec& a, const Rec& b) {
                if (a.ts != b.ts) return a.ts > b.ts;
                return a.id > b.id;
            });
            std::string out = "[";
            for (size_t i = 0; i < recs.size(); ++i) {
                if (i) out += ",";
                out += recs[i].val;
            }
            out += "]";
            return dup(out);
        }
    }

    // MySQL 兜底（每人每题最后一发）
    if (oj::mysql_available()) {
        std::string out;
        if (oj::mysql_contest_submissions(cid, uname, view_all != 0, out)) return dup(out);
    }
    return dup("[]");
}
// ===== MySQL 用户体系 =====

OJ_API int oj_init_mysql(const char* host, int port, const char* user,
                         const char* pass, const char* db,
                         const char* problem_dir, const char* data_dir) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    g_problemDir = problem_dir ? problem_dir : "";
    g_dataDir = data_dir ? data_dir : "ojdata";
    g_tempDir = g_dataDir + "\\temp";
    CreateDirectoryA(g_dataDir.c_str(), NULL);
    CreateDirectoryA(g_tempDir.c_str(), NULL);

    std::string err;
    bool ok = oj::mysql_connect(host ? host : "localhost", (unsigned)port,
                                user ? user : "root", pass ? pass : "",
                                db ? db : "oj", err);
    // 提交记录持久化由 oj_init_redis 连接远端 LSM（RESP）完成，此处不依赖本地存储
    g_submitSeq = 0;
    return ok ? 0 : 1;   // 0 = MySQL 已连接；1 = 本地回退模式
}

// 初始化中间层 HTTP 模式（middlewareUrl 非空时使用，替代直连 MySQL）。
OJ_API int oj_init_middleware(const char* url, const char* problem_dir, const char* data_dir) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    g_problemDir = problem_dir ? problem_dir : "";
    g_dataDir = data_dir ? data_dir : "ojdata";
    g_tempDir = g_dataDir + "\\temp";
    CreateDirectoryA(g_dataDir.c_str(), NULL);
    CreateDirectoryA(g_tempDir.c_str(), NULL);
    g_submitSeq = 0;
    oj::middleware_set_url(url ? url : "");
    return 0;
}

OJ_API const char* oj_mysql_init_schema(void) {
    std::string err;
    if (oj::mysql_init_schema(err)) return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

// ===== 题目 / 比赛发布与同步（MySQL 分发） =====

OJ_API const char* oj_mysql_publish_problem(int id, const char* problem_dir, int is_public) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用（libmysql.dll 未加载）\"}");
    std::string dir = problem_dir ? problem_dir : "";
    if (dir.empty() || !exists(dir)) return dup("{\"ok\":false,\"error\":\"题目目录不存在\"}");

    std::string st = readFile(dir + "\\statement.txt");
    size_t nl = st.find('\n');
    std::string title = (nl == std::string::npos) ? st : st.substr(0, nl);
    std::string desc  = (nl == std::string::npos) ? "" : st.substr(nl + 1);

    // 样例：来自勾选的生成器第 1 组数据（sample_gen.txt 存生成器名），无则回退旧 sample.in/out
    auto trimStr = [](const std::string& s) {
        size_t a = s.find_first_not_of(" \t\r\n");
        if (a == std::string::npos) return std::string();
        size_t b = s.find_last_not_of(" \t\r\n");
        return s.substr(a, b - a + 1);
    };
    std::string sampleIn, sampleOut;
    std::string sampleGen = trimStr(readFile(dir + "\\sample_gen.txt"));
    if (!sampleGen.empty()) {
        sampleIn  = readFile(dir + "\\" + sampleGen + "\\1.in");
        sampleOut = readFile(dir + "\\" + sampleGen + "\\1.out");
    } else {
        sampleIn  = readFile(dir + "\\sample.in");
        sampleOut = readFile(dir + "\\sample.out");
    }
    std::string stdCode   = readFile(dir + "\\std.cpp");
    auto meta = readMeta(dir);

    std::string err;
    if (!oj::mysql_upsert_problem(id, title, desc, sampleIn, sampleOut,
                                  (int)meta.timeLimitMs, (int)meta.memLimitMB,
                                  meta.tagsJson.empty() ? "[]" : meta.tagsJson, stdCode,
                                  is_public != 0, err))
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");

    // 生成器绑定由「数据生成器」页直接维护（problem_generators），发布只更新题面/元数据/标程。
    return dup("{\"ok\":true}");
}

OJ_API const char* oj_mysql_publish_contest(int cid, const char* contest_json) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用（libmysql.dll 未加载）\"}");
    std::string j = contest_json ? contest_json : "";
    std::string name = jsonUnescape(jsonStr(j, "name"));
    std::string desc = jsonUnescape(jsonStr(j, "description"));
    std::string start = jsonUnescape(jsonStr(j, "startTime"));
    std::string end = jsonUnescape(jsonStr(j, "endTime"));
    std::vector<int> pids = parseIntArray(j, "problems");
    std::string pjson = "[";
    for (size_t i = 0; i < pids.size(); ++i) {
        if (i) pjson += ",";
        pjson += std::to_string(pids[i]);
    }
    pjson += "]";

    // 重建与 authorcore 一致格式的 contest.json（id/name/description/startTime/endTime/problems）
    std::string json = "{\"id\":" + std::to_string(cid)
        + ",\"name\":\"" + jsonEscape(name) + "\""
        + ",\"description\":\"" + jsonEscape(desc) + "\""
        + ",\"startTime\":\"" + jsonEscape(start) + "\""
        + ",\"endTime\":\"" + jsonEscape(end) + "\""
        + ",\"problems\":" + pjson + "}";

    std::string err;
    if (!oj::mysql_upsert_contest(cid, json, err))
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    return dup("{\"ok\":true}");
}

OJ_API const char* oj_mysql_sync_problems(void) {
    std::string err;
    if (!oj::mysql_sync_problems(g_problemDir, serverRoot(), err))
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    return dup("{\"ok\":true}");
}

OJ_API const char* oj_ensure_problem_data(int problem_id) {
    std::string err;
    if (!oj::ensure_problem_data(problem_id, g_problemDir, err))
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    return dup("{\"ok\":true}");
}

OJ_API const char* oj_mysql_problem_visibility(void) {
    if (!oj::mysql_available()) return dup("{}");
    std::string out;
    if (oj::mysql_problem_visibility(out)) return dup(out);
    return dup("{}");
}

OJ_API const char* oj_mysql_list_problems(void) {
    if (!oj::mysql_available()) return dup("[]");
    std::string out;
    if (oj::mysql_list_problems(out)) return dup(out);
    return dup("[]");
}

OJ_API const char* oj_mysql_get_problem(int id) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用\"}");
    std::string out;
    if (oj::mysql_get_problem(id, out)) return dup(out);
    return dup("{\"ok\":false,\"error\":\"查询失败\"}");
}

// ===== 生成器库（全局 generators 表） =====

OJ_API const char* oj_mysql_list_generators(void) {
    if (!oj::mysql_available()) return dup("[]");
    std::string out;
    if (oj::mysql_list_generators(out)) return dup(out);
    return dup("[]");
}

OJ_API const char* oj_mysql_get_generator(int id) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用\"}");
    std::string name, code, desc;
    if (oj::mysql_get_generator(id, name, code, desc))
        return dup("{\"ok\":true,\"id\":" + std::to_string(id)
                 + ",\"name\":\"" + jsonEscape(name) + "\""
                 + ",\"code\":\"" + jsonEscape(code) + "\""
                 + ",\"description\":\"" + jsonEscape(desc) + "\"}");
    return dup("{\"ok\":false,\"error\":\"生成器不存在\"}");
}

OJ_API const char* oj_mysql_create_generator(const char* name, const char* code, const char* description) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用\"}");
    std::string err;
    long long id = 0;
    if (oj::mysql_create_generator(name ? name : "", code ? code : "", description ? description : "", id, err))
        return dup("{\"ok\":true,\"id\":" + std::to_string(id) + "}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API const char* oj_mysql_update_generator(int id, const char* code, const char* description) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用\"}");
    std::string err;
    if (oj::mysql_update_generator(id, code ? code : "", description ? description : "", err))
        return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API const char* oj_mysql_problem_generators(int problem_id) {
    if (!oj::mysql_available()) return dup("[]");
    std::string out;
    if (oj::mysql_problem_generators(problem_id, out)) return dup(out);
    return dup("[]");
}

OJ_API const char* oj_mysql_bind_generator(int problem_id, int generator_id, int gen_count) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用\"}");
    std::string err;
    if (oj::mysql_bind_generator(problem_id, generator_id, gen_count, err))
        return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API const char* oj_mysql_unbind_generator(int problem_id, int generator_id) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用\"}");
    if (oj::mysql_unbind_generator(problem_id, generator_id))
        return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"解绑失败\"}");
}

OJ_API const char* oj_mysql_search_generators(const char* keyword) {
    if (!oj::mysql_available()) return dup("[]");
    std::string out;
    if (oj::mysql_search_generators(keyword ? keyword : "", out)) return dup(out);
    return dup("[]");
}

OJ_API const char* oj_mysql_set_testcase(int problem_id, const char* name, const char* input, const char* output, int is_sample) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用\"}");
    if (oj::mysql_set_testcase(problem_id, name ? name : "", input ? input : "",
                               output ? output : "", is_sample != 0))
        return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"写入测试样例失败\"}");
}

OJ_API const char* oj_mysql_remove_testcase(int problem_id, const char* name) {
    if (!oj::mysql_available()) return dup("{\"ok\":false,\"error\":\"MySQL 不可用\"}");
    if (oj::mysql_remove_testcase(problem_id, name ? name : "")) return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"删除测试样例失败\"}");
}

OJ_API const char* oj_mysql_list_testcases(int problem_id) {
    if (!oj::mysql_available()) return dup("[]");
    std::string out;
    if (oj::mysql_list_testcases(problem_id, out)) return dup(out);
    return dup("[]");
}

OJ_API const char* oj_debug_run(const char* code, const char* input, int timeout_ms) {
    if (!code || !*code) return dup("{\"ok\":false,\"error\":\"代码为空\"}");
    DWORD t = timeout_ms > 0 ? (DWORD)timeout_ms : 2000;
    oj::DebugResult r = oj::debug_run(g_tempDir, code, input ? input : "", t);
    std::string json = "{\"ok\":" + std::string(r.ok ? "true" : "false")
        + ",\"compileError\":\"" + jsonEscape(r.compileError) + "\""
        + ",\"output\":\"" + jsonEscape(r.output) + "\""
        + ",\"runError\":\"" + jsonEscape(r.runError) + "\""
        + ",\"timeout\":" + (r.timeout ? "true" : "false")
        + ",\"exitCode\":" + std::to_string(r.exitCode) + "}";
    return dup(json);
}

OJ_API const char* oj_debug_test(const char* code, int problem_id, int timeout_ms) {
    if (!code || !*code) return dup("{\"ok\":false,\"error\":\"代码为空\"}");
    DWORD t = timeout_ms > 0 ? (DWORD)timeout_ms : 2000;
    std::string dir = g_problemDir + "\\" + std::to_string(problem_id);
    std::string inFile = dir + "\\sample.in";
    std::string outFile = dir + "\\sample.out";
    oj::DebugTestResult r = oj::debug_test(g_tempDir, code, inFile, outFile, t);
    std::string json = "{\"ok\":" + std::string(r.ok ? "true" : "false")
        + ",\"compileError\":\"" + jsonEscape(r.compileError) + "\""
        + ",\"output\":\"" + jsonEscape(r.output) + "\""
        + ",\"expected\":\"" + jsonEscape(r.expected) + "\""
        + ",\"passed\":" + (r.passed ? "true" : "false")
        + ",\"runError\":\"" + jsonEscape(r.runError) + "\""
        + ",\"timeout\":" + (r.timeout ? "true" : "false")
        + ",\"exitCode\":" + std::to_string(r.exitCode) + "}";
    return dup(json);
}

OJ_API const char* oj_register(const char* username, const char* password, const char* role) {
    std::string err;
    if (oj::mysql_register(username ? username : "", password ? password : "",
                           role ? role : "user", err))
        return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API const char* oj_login(const char* username, const char* password) {
    std::string token, role, uname, nickname, avatar, err;
    long long uid = 0;
    if (oj::mysql_login(username ? username : "", password ? password : "",
                        token, uid, role, uname, nickname, avatar, err)) {
        return dup(std::string("{\"ok\":true,\"token\":\"" + token + "\"")
                 + ",\"userId\":" + std::to_string(uid)
                 + ",\"role\":\"" + role + "\""
                 + ",\"username\":\"" + jsonEscape(uname) + "\""
                 + ",\"nickname\":\"" + jsonEscape(nickname) + "\""
                 + ",\"avatar\":\"" + jsonEscape(avatar) + "\"}");
    }
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API const char* oj_whoami(const char* token) {
    std::string role, uname, nickname, avatar, err;
    long long uid = 0;
    if (oj::mysql_whoami(token ? token : "", uid, role, uname, nickname, avatar, err)) {
        return dup(std::string("{\"ok\":true,\"userId\":" + std::to_string(uid))
                 + ",\"role\":\"" + role + "\""
                 + ",\"username\":\"" + jsonEscape(uname) + "\""
                 + ",\"nickname\":\"" + jsonEscape(nickname) + "\""
                 + ",\"avatar\":\"" + jsonEscape(avatar) + "\"}");
    }
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

// 更新当前用户资料（昵称 / 头像，头像为 data URL 或空串）
OJ_API const char* oj_update_profile(const char* token, const char* nickname, const char* avatar) {
    std::string role, uname, nk, av, err;
    long long uid = 0;
    if (!oj::mysql_whoami(token ? token : "", uid, role, uname, nk, av, err)) {
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err.empty() ? "会话无效或已过期" : err) + "\"}");
    }
    if (!oj::mysql_update_profile(uid, nickname ? nickname : "", avatar ? avatar : "", err))
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    return dup("{\"ok\":true}");
}

OJ_API const char* oj_logout(const char* token) {
    oj::mysql_logout(token ? token : "");
    return dup("{\"ok\":true}");
}

OJ_API const char* oj_list_users(const char* token) {
    std::string role, uname, nickname, avatar, err;
    long long uid = 0;
    if (!oj::mysql_whoami(token ? token : "", uid, role, uname, nickname, avatar, err) || role != "admin") {
        return dup("{\"ok\":false,\"error\":\"需要 admin 权限\"}");
    }
    std::string out;
    if (oj::mysql_list_users(out, err)) return dup(out);
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API void oj_free_string(const char* s) {
    if (s) free((void*)s);
}

} // extern "C"
