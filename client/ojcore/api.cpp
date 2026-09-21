// api.cpp — 导出接口实现
#include "ojcore.h"
#include "judge.h"
#include "mysql_dao.h"

#include "lsm/engine.h"
#include "lsm/level_iterator.h"

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
std::unique_ptr<tiny_lsm::LSM> g_lsm;   // tiny-lsm 引擎（lsm_shared.dll）
long long   g_submitSeq = 0;

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
    long long version = 0;
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
    if (findVal("version", v), !v.empty()) m.version = atoll(v.c_str());
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
    long long timeLimitMs, memLimitMB, version;
    std::string tagsJson;
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
        auto meta = readMeta(dir);
        pi.timeLimitMs = meta.timeLimitMs;
        pi.memLimitMB = meta.memLimitMB;
        pi.version = meta.version;
        pi.tagsJson = meta.tagsJson;
        res.push_back(pi);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    return res;
}

// ---- native 异常防护 ----
// 任何 native 异常（C++ 异常 / 访问违例）都转成错误码返回，
// 绝不穿过 P/Invoke 边界导致宿主进程崩溃。
static int lsm_init_cpp(const std::string& dataDir, std::string& errOut) {
    try {
        tiny_lsm::LSM* p = new tiny_lsm::LSM(dataDir);
        g_lsm.reset(p);
        return 0;
    } catch (const std::exception& e) {
        errOut = e.what();
        return -1;
    } catch (...) {
        errOut = "unknown C++ exception during LSM init";
        return -1;
    }
}

static int init_lsm_safe(const std::string& dataDir, std::string& errOut) {
    __try {
        return lsm_init_cpp(dataDir, errOut);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        errOut = "native access violation during LSM init";
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

    std::string detail, verdict;
    std::vector<oj::CaseResult> cases;

    std::string compileErr;
    oj::JudgeOptions jopt;
    // 从题目 meta.json 读取时间/内存限制（默认 1000ms / 256MB）
    auto meta = readMeta(problemDir);
    jopt.timeoutMs = (DWORD)meta.timeLimitMs;
    jopt.memBytes = (SIZE_T)(meta.memLimitMB) * 1024 * 1024;
    std::string spjSrc = problemDir + "\\spj.cpp";
    if (!oj::compile_cpp(src, exe, compileErr)) {
        verdict = "CE";
        detail = compileErr.empty() ? "编译失败" : compileErr;
    } else {
        // 题目目录有 spj.cpp 则编译并启用 Special Judge
        if (exists(spjSrc)) {
            std::string spjExe = g_tempDir + "\\spj.exe";
            std::string spjErr;
            if (!oj::compile_cpp(spjSrc, spjExe, spjErr)) {
                verdict = "CE";
                detail = "spj 编译失败: " + (spjErr.empty() ? "未知错误" : spjErr);
            } else {
                jopt.spjExe = spjExe;
            }
        }
        if (verdict.empty()) {
            cases = oj::run_tests(exe, problemDir, out, jopt);
            verdict = oj::summarize(cases);
            int ac = 0; for (auto& c : cases) if (c.passed) ac++;
            detail = "通过 " + std::to_string(ac) + "/" + std::to_string(cases.size()) + " 个测试点";
        }
        DeleteFileA(exe.c_str());
        if (!jopt.spjExe.empty()) DeleteFileA(jopt.spjExe.c_str());
    }

    long long sid = ++g_submitSeq;

    // 提交时间戳
    SYSTEMTIME st;
    GetLocalTime(&st);
    char tsBuf[32];
    snprintf(tsBuf, sizeof(tsBuf), "%04d-%02d-%02d %02d:%02d:%02d",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);

    // 拼结果 JSON（含 problemId / username / virtual / ts 供按题查询历史提交与榜单聚合）
    std::string json = "{\"id\":" + std::to_string(sid)
        + ",\"problemId\":" + std::to_string(problem_id)
        + ",\"contestId\":" + std::to_string(contest_id)
        + ",\"username\":\"" + jsonEscape(username) + "\""
        + ",\"virtual\":" + (virtual_ ? "true" : "false")
        + ",\"verdict\":\"" + verdict + "\""
        + ",\"detail\":\"" + jsonEscape(detail) + "\""
        + ",\"ts\":\"" + tsBuf + "\""
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

    // 落 tiny-lsm（WAL 先落盘再进 MemTable，超阈值刷 SST）；失败不影响判题结果返回
    try {
        std::string sidKey = "submission:" + std::to_string(sid);
        g_lsm->put(sidKey, json);
        g_lsm->put("meta:seq", std::to_string(g_submitSeq));
    } catch (...) { /* 存储失败仅记录，判题结果照常返回 */ }

    // 落 MySQL：同一用户同一题重复提交时，最后结果原地更新（upsert，不追加历史行）
    // 用户名按登录账号解析；未登录/查无此人回退到 anonymous 默认用户。
    // MySQL 未连接或写入失败不影响判题结果返回。
    try {
        if (oj::mysql_available()) {
            long long uid = 0;
            std::string uname = (username && *username) ? username : "anonymous";
            if (!oj::mysql_user_id_by_name(uname, uid)) {
                if (!oj::mysql_user_id_by_name("anonymous", uid)) uid = 0;
            }
            if (uid > 0) {
                int totalMs = 0;
                for (auto& c : cases) totalMs += (int)c.timeMs;
                oj::mysql_upsert_submission(uid, problem_id, contest_id, verdict, detail,
                                            totalMs, virtual_ != 0, tsBuf);
            }
        }
    } catch (...) { /* MySQL 写入失败仅记录，判题结果照常返回 */ }

    return dup(json);
}

} // namespace

extern "C" {

OJ_API int oj_init(const char* problem_dir, const char* data_dir) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    g_problemDir = problem_dir ? problem_dir : "";
    g_dataDir = data_dir ? data_dir : "";
    g_tempDir = g_dataDir + "\\temp";
    CreateDirectoryA(g_dataDir.c_str(), NULL);
    CreateDirectoryA(g_tempDir.c_str(), NULL);
    // 打开 tiny-lsm 引擎（SEH 保护，任何 native 异常都不穿过 P/Invoke）
    g_lastError.clear();
    int rc = init_lsm_safe(g_dataDir, g_lastError);
    if (rc != 0) return rc;
    // 从 LSM 恢复提交序号
    g_submitSeq = 0;
    try {
        auto seq = g_lsm->get("meta:seq");
        if (seq.has_value()) g_submitSeq = atoll(seq->c_str());
    } catch (...) { g_submitSeq = 0; }
    return 0;
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
             + ",\"version\":" + std::to_string(p.version) + "}";
    }
    json += "]";
    return dup(json);
}

OJ_API const char* oj_get_submissions(int problem_id) {
    std::string json = "[";
    bool first = true;
    try {
        // 遍历 LSM 全部记录，取 submission:* 且 problemId 匹配
        auto it = g_lsm->begin(0);
        auto end = g_lsm->end();
        for (; it != end; ++it) {
            auto kv = *it;
            std::string key = kv.first;
            if (key.rfind("submission:", 0) != 0) continue;
            // 解析 value 中的 "problemId":N
            std::string val = kv.second;
            std::string mark = "\"problemId\":";
            size_t p = val.find(mark);
            if (p == std::string::npos) continue;
            long long pid = atoll(val.c_str() + p + mark.size());
            if (pid != problem_id) continue;
            if (!first) json += ",";
            first = false;
            json += val;
        }
    } catch (...) { /* 遍历失败返回已收集部分 */ }
    json += "]";
    return dup(json);
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

    // MySQL 可用时榜单直接查库（跨进程持久），失败再回退 LSM
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

    // 2. 收集每个人每题的 AC 信息
    struct Cell { int wrong = 0; long long acMin = -1; };   // acMin<0 表示未 AC
    struct UserStat {
        std::string username;
        std::map<int, Cell> cells;
    };
    std::map<std::string, UserStat> offUsers, virtUsers;

    auto consider = [&](std::map<std::string, UserStat>& table,
                        const std::string& uname, int pid,
                        bool ac, const std::string& ts) {
        if (table.find(uname) == table.end()) table[uname].username = uname;
        Cell& c = table[uname].cells[pid];
        if (c.acMin >= 0) return;          // 已 AC 不再计分
        if (ac) {
            time_t t = parseTime(ts);
            c.acMin = (startT > 0 && t > 0) ? (long long)difftime(t, startT) / 60 : 0;
        } else {
            c.wrong++;
        }
    };

    try {
        auto it = g_lsm->begin(0);
        auto end = g_lsm->end();
        for (; it != end; ++it) {
            auto kv = *it;
            if (kv.first.rfind("submission:", 0) != 0) continue;
            std::string val = kv.second;
            long long pid = jsonInt(val, "problemId");
            long long recCid = jsonInt(val, "contestId", 0);
            bool inContest;
            if (recCid > 0) {
                inContest = (recCid == cid);          // 新记录：按 contestId 精确归属
            } else {
                inContest = false;                    // 老记录回退：按题目集合归属
                for (int x : pids) if (x == pid) { inContest = true; break; }
            }
            if (!inContest) continue;
            std::string verdict = jsonStr(val, "verdict");
            bool ac = (verdict == "AC");
            std::string uname = jsonStr(val, "username");
            if (uname.empty()) uname = "anonymous";
            std::string ts = jsonStr(val, "ts");
            bool virt = val.find("\"virtual\":true") != std::string::npos;
            consider(virt ? virtUsers : offUsers, uname, (int)pid, ac, ts);
        }
    } catch (...) {}

    // 3. 聚合输出
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

    std::string json = "{\"official\":" + emitTable(offUsers)
                     + ",\"virtual\":"  + emitTable(virtUsers) + "}";
    return dup(json);
}

// ===== 比赛报名 / 比赛提交记录 =====

static std::string contestRegKey(int cid, const std::string& uname) {
    return "reg:" + std::to_string(cid) + ":" + uname;
}

// 报名（幂等）。MySQL 可用时写 contest_registrations；LSM 始终写一份本地凭证。
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
        g_lsm->put(contestRegKey(cid, uname), val);
        ok = true;   // LSM 成功即视为报名成功
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
            auto vv = g_lsm->get(contestRegKey(cid, uname));
            if (vv.has_value() && !vv->empty()) {
                registered = true;
                virt = vv->find("\"virtual\":true") != std::string::npos;
            }
        } catch (...) {}
    }
    return dup(std::string("{\"ok\":true,\"registered\":") + (registered ? "true" : "false")
             + ",\"virtual\":" + (virt ? "true" : "false") + "}");
}

// 比赛提交记录（按 contestId 精确归属，老记录回退题目集合），时间倒序
OJ_API const char* oj_contest_submissions(int cid) {
    // MySQL 可用时提交记录直接查库（每人每题最后一次结果），失败回退 LSM 全量历史
    if (oj::mysql_available()) {
        std::string out;
        if (oj::mysql_contest_submissions(cid, out)) return dup(out);
    }
    std::string craw = readFile(serverRoot() + "\\contests\\" + std::to_string(cid) + "\\contest.json");
    std::vector<int> pids;
    if (!craw.empty()) pids = parseIntArray(craw, "problems");

    struct Rec { long long id; std::string ts; std::string val; };
    std::vector<Rec> recs;
    try {
        auto it = g_lsm->begin(0);
        auto end = g_lsm->end();
        for (; it != end; ++it) {
            auto kv = *it;
            if (kv.first.rfind("submission:", 0) != 0) continue;
            const std::string& val = kv.second;
            long long pid = jsonInt(val, "problemId");
            long long recCid = jsonInt(val, "contestId", 0);
            bool inContest;
            if (recCid > 0) {
                inContest = (recCid == cid);
            } else {
                inContest = false;
                for (int x : pids) if (x == pid) { inContest = true; break; }
            }
            if (!inContest) continue;
            Rec r;
            r.id = jsonInt(val, "id");
            r.ts = jsonStr(val, "ts");
            r.val = val;
            recs.push_back(std::move(r));
        }
    } catch (...) {}
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
// ===== MySQL 用户体系 =====

OJ_API int oj_init_mysql(const char* host, int port, const char* user,
                         const char* pass, const char* db,
                         const char* problem_dir) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    g_problemDir = problem_dir ? problem_dir : "";
    g_dataDir = "ojdata";
    g_tempDir = g_dataDir + "\\temp";
    CreateDirectoryA(g_dataDir.c_str(), NULL);
    CreateDirectoryA(g_tempDir.c_str(), NULL);

    std::string err;
    bool ok = oj::mysql_connect(host ? host : "localhost", (unsigned)port,
                                user ? user : "root", pass ? pass : "",
                                db ? db : "oj", err);
    // 本地 LSM 仍初始化（用于提交记录兜底）
    g_lastError.clear();
    int rc = init_lsm_safe(g_dataDir, g_lastError);
    if (rc != 0) return rc;
    try {
        auto seq = g_lsm->get("meta:seq");
        if (seq.has_value()) g_submitSeq = atoll(seq->c_str());
    } catch (...) { g_submitSeq = 0; }
    return ok ? 0 : 1;   // 0 = MySQL 已连接；1 = 本地回退模式
}

OJ_API const char* oj_mysql_init_schema(void) {
    std::string err;
    if (oj::mysql_init_schema(err)) return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API const char* oj_register(const char* username, const char* password, const char* role) {
    std::string err;
    if (oj::mysql_register(username ? username : "", password ? password : "",
                           role ? role : "user", err))
        return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API const char* oj_login(const char* username, const char* password) {
    std::string token, role, uname, err;
    long long uid = 0;
    if (oj::mysql_login(username ? username : "", password ? password : "",
                        token, uid, role, uname, err)) {
        return dup(std::string("{\"ok\":true,\"token\":\"" + token + "\"")
                 + ",\"userId\":" + std::to_string(uid)
                 + ",\"role\":\"" + role + "\""
                 + ",\"username\":\"" + uname + "\"}");
    }
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API const char* oj_whoami(const char* token) {
    std::string role, uname, err;
    long long uid = 0;
    if (oj::mysql_whoami(token ? token : "", uid, role, uname, err)) {
        return dup(std::string("{\"ok\":true,\"userId\":" + std::to_string(uid))
                 + ",\"role\":\"" + role + "\""
                 + ",\"username\":\"" + uname + "\"}");
    }
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

OJ_API const char* oj_logout(const char* token) {
    oj::mysql_logout(token ? token : "");
    return dup("{\"ok\":true}");
}

OJ_API const char* oj_list_users(const char* token) {
    std::string role, uname, err;
    long long uid = 0;
    if (!oj::mysql_whoami(token ? token : "", uid, role, uname, err) || role != "admin") {
        return dup("{\"ok\":false,\"error\":\"需要 admin 权限\"}");
    }
    std::string out;
    if (oj::mysql_list_users(out, err)) return dup(out);
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

// ===== 题目数据生成器（LSM 历史版本 + MySQL 最新版本 upsert） =====

static std::string genVersionKey(int pid, int version) {
    char buf[64];
    snprintf(buf, sizeof(buf), "gen_version:%d:%08d", pid, version);
    return buf;
}

static std::string genSeqKey(int pid) {
    return "gen_seq:" + std::to_string(pid);
}

static int genNextVersion(int pid) {
    int cur = 0;
    try {
        auto v = g_lsm->get(genSeqKey(pid));
        if (v.has_value()) cur = atoi(v->c_str());
    } catch (...) {}
    return cur + 1;
}

static std::string nowTs() {
    SYSTEMTIME st; GetLocalTime(&st);
    char buf[32];
    snprintf(buf, sizeof(buf), "%04d-%02d-%02d %02d:%02d:%02d",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

static int countLines(const std::string& s) {
    int n = 1;
    for (char c : s) if (c == '\n') n++;
    return n;
}

static std::string firstLine(const std::string& s) {
    size_t p = s.find('\n');
    std::string line = (p == std::string::npos) ? s : s.substr(0, p);
    if (line.size() > 80) line = line.substr(0, 80);
    return line;
}

OJ_API const char* oj_gen_save(int problem_id, const char* code) {
    std::string src = code ? code : "";
    int version = genNextVersion(problem_id);
    std::string ts = nowTs();
    // 1. 写 LSM 历史版本
    std::string val = "{\"pid\":" + std::to_string(problem_id)
        + ",\"version\":" + std::to_string(version)
        + ",\"ts\":\"" + ts + "\""
        + ",\"code\":\"" + jsonEscape(src) + "\"}";
    try {
        g_lsm->put(genVersionKey(problem_id, version), val);
        g_lsm->put(genSeqKey(problem_id), std::to_string(version));
    } catch (...) { /* LSM 失败不阻塞 */ }
    // 2. MySQL upsert 最新版本（重复 problem_id 原地替换）
    std::string err;
    bool mysqlOk = false;
    try {
        if (oj::mysql_available()) {
            mysqlOk = oj::mysql_gen_upsert(problem_id, src, version, ts, err);
        }
    } catch (...) {}
    (void)mysqlOk;
    return dup(std::string("{\"ok\":true,\"version\":") + std::to_string(version) + "}");
}

OJ_API const char* oj_gen_get_current(int problem_id) {
    // 优先 MySQL
    std::string code, ua, err;
    int ver = 0;
    try {
        if (oj::mysql_available() && oj::mysql_gen_get(problem_id, code, ver, ua, err)) {
            return dup(std::string("{\"ok\":true,\"version\":") + std::to_string(ver)
                     + ",\"updatedAt\":\"" + ua + "\""
                     + ",\"code\":\"" + jsonEscape(code) + "\"}");
        }
    } catch (...) {}
    // 回退 LSM：找最大版本号
    int maxVer = 0;
    std::string maxVal;
    try {
        std::string prefix = "gen_version:" + std::to_string(problem_id) + ":";
        auto it = g_lsm->begin(0);
        auto end = g_lsm->end();
        for (; it != end; ++it) {
            auto kv = *it;
            if (kv.first.rfind(prefix, 0) != 0) continue;
            int v = atoi(kv.first.substr(prefix.size()).c_str());
            if (v > maxVer) { maxVer = v; maxVal = kv.second; }
        }
    } catch (...) {}
    if (maxVer == 0) return dup("{\"ok\":false}");
    // 从 maxVal 提取 code 和 ts
    std::string outCode, outTs;
    size_t cp = maxVal.find("\"code\":\"");
    if (cp != std::string::npos) {
        cp += 8;
        size_t ce = maxVal.find("\"", cp);
        // 处理转义
        std::string raw = maxVal.substr(cp, ce - cp);
        for (size_t i = 0; i < raw.size(); ++i) {
            if (raw[i] == '\\' && i + 1 < raw.size()) {
                char n = raw[i + 1];
                if (n == 'n') outCode += '\n';
                else if (n == 'r') outCode += '\r';
                else if (n == 't') outCode += '\t';
                else if (n == '"') outCode += '"';
                else if (n == '\\') outCode += '\\';
                else outCode += n;
                i++;
            } else outCode += raw[i];
        }
    }
    size_t tp = maxVal.find("\"ts\":\"");
    if (tp != std::string::npos) {
        tp += 6;
        size_t te = maxVal.find("\"", tp);
        outTs = maxVal.substr(tp, te - tp);
    }
    return dup(std::string("{\"ok\":true,\"version\":") + std::to_string(maxVer)
             + ",\"updatedAt\":\"" + outTs + "\""
             + ",\"code\":\"" + jsonEscape(outCode) + "\"}");
}

OJ_API const char* oj_gen_list_versions(int problem_id) {
    struct Ver { int version; std::string ts; int lines; std::string summary; };
    std::vector<Ver> vers;
    try {
        std::string prefix = "gen_version:" + std::to_string(problem_id) + ":";
        auto it = g_lsm->begin(0);
        auto end = g_lsm->end();
        for (; it != end; ++it) {
            auto kv = *it;
            if (kv.first.rfind(prefix, 0) != 0) continue;
            int v = atoi(kv.first.substr(prefix.size()).c_str());
            // 从 value 提取 code/ts
            std::string code, ts;
            size_t cp = kv.second.find("\"code\":\"");
            if (cp != std::string::npos) {
                cp += 8;
                size_t ce = kv.second.find("\"", cp);
                std::string raw = kv.second.substr(cp, ce - cp);
                for (size_t i = 0; i < raw.size(); ++i) {
                    if (raw[i] == '\\' && i + 1 < raw.size()) {
                        char n = raw[i + 1];
                        if (n == 'n') code += '\n';
                        else if (n == 'r') code += '\r';
                        else if (n == 't') code += '\t';
                        else if (n == '"') code += '"';
                        else if (n == '\\') code += '\\';
                        else code += n;
                        i++;
                    } else code += raw[i];
                }
            }
            size_t tp = kv.second.find("\"ts\":\"");
            if (tp != std::string::npos) {
                tp += 6;
                size_t te = kv.second.find("\"", tp);
                ts = kv.second.substr(tp, te - tp);
            }
            Ver vv; vv.version = v; vv.ts = ts;
            vv.lines = countLines(code);
            vv.summary = firstLine(code);
            vers.push_back(vv);
        }
    } catch (...) {}
    std::sort(vers.begin(), vers.end(), [](const Ver& a, const Ver& b) { return a.version > b.version; });
    std::string json = "[";
    for (size_t i = 0; i < vers.size(); ++i) {
        if (i) json += ",";
        json += "{\"version\":" + std::to_string(vers[i].version)
             + ",\"ts\":\"" + vers[i].ts + "\""
             + ",\"lines\":" + std::to_string(vers[i].lines)
             + ",\"summary\":\"" + jsonEscape(vers[i].summary) + "\"}";
    }
    json += "]";
    return dup(json);
}

OJ_API const char* oj_gen_get_version(int problem_id, int version) {
    try {
        auto v = g_lsm->get(genVersionKey(problem_id, version));
        if (!v.has_value()) return dup("{\"ok\":false}");
        std::string code, ts;
        size_t cp = v->find("\"code\":\"");
        if (cp != std::string::npos) {
            cp += 8;
            size_t ce = v->find("\"", cp);
            std::string raw = v->substr(cp, ce - cp);
            for (size_t i = 0; i < raw.size(); ++i) {
                if (raw[i] == '\\' && i + 1 < raw.size()) {
                    char n = raw[i + 1];
                    if (n == 'n') code += '\n';
                    else if (n == 'r') code += '\r';
                    else if (n == 't') code += '\t';
                    else if (n == '"') code += '"';
                    else if (n == '\\') code += '\\';
                    else code += n;
                    i++;
                } else code += raw[i];
            }
        }
        size_t tp = v->find("\"ts\":\"");
        if (tp != std::string::npos) {
            tp += 6;
            size_t te = v->find("\"", tp);
            ts = v->substr(tp, te - tp);
        }
        return dup(std::string("{\"ok\":true,\"version\":") + std::to_string(version)
                 + ",\"ts\":\"" + ts + "\""
                 + ",\"code\":\"" + jsonEscape(code) + "\"}");
    } catch (...) { return dup("{\"ok\":false}"); }
}

OJ_API const char* oj_gen_search(const char* keyword) {
    std::string kw = keyword ? keyword : "";
    // 优先 MySQL
    std::string out, err;
    try {
        if (oj::mysql_available() && oj::mysql_gen_search(kw, out, err)) {
            return dup(out);
        }
    } catch (...) {}
    // 回退 LSM：遍历所有 gen_version:*，取每题最新版本匹配
    std::string kwLow;
    for (char c : kw) kwLow += (char)tolower((unsigned char)c);
    std::map<int, std::pair<int, std::string>> latest; // pid -> (version, value)
    try {
        auto it = g_lsm->begin(0);
        auto end = g_lsm->end();
        for (; it != end; ++it) {
            auto kv = *it;
            if (kv.first.rfind("gen_version:", 0) != 0) continue;
            // 解析 pid
            size_t colon1 = kv.first.find(':', 12);
            size_t colon2 = kv.first.find(':', colon1 + 1);
            int pid = atoi(kv.first.substr(colon1 + 1, colon2 - colon1 - 1).c_str());
            int ver = atoi(kv.first.substr(colon2 + 1).c_str());
            auto& cur = latest[pid];
            if (ver > cur.first) { cur.first = ver; cur.second = kv.second; }
        }
    } catch (...) {}
    std::string json = "[";
    bool first = true;
    for (auto& kv : latest) {
        int pid = kv.first;
        int ver = kv.second.first;
        const std::string& val = kv.second.second;
        // 提取 code
        std::string code;
        size_t cp = val.find("\"code\":\"");
        if (cp != std::string::npos) {
            cp += 8;
            size_t ce = val.find("\"", cp);
            std::string raw = val.substr(cp, ce - cp);
            for (size_t i = 0; i < raw.size(); ++i) {
                if (raw[i] == '\\' && i + 1 < raw.size()) {
                    char n = raw[i + 1];
                    if (n == 'n') code += '\n';
                    else if (n == 'r') code += '\r';
                    else if (n == 't') code += '\t';
                    else if (n == '"') code += '"';
                    else if (n == '\\') code += '\\';
                    else code += n;
                    i++;
                } else code += raw[i];
            }
        }
        std::string codeLow;
        for (char c : code) codeLow += (char)tolower((unsigned char)c);
        if (!kwLow.empty() && codeLow.find(kwLow) == std::string::npos) continue;
        // 预览前 3 行
        std::string preview;
        int lines = 0;
        for (char c : code) {
            if (c == '\n') { lines++; if (lines >= 3) break; }
            preview += c;
        }
        std::string ts;
        size_t tp = val.find("\"ts\":\"");
        if (tp != std::string::npos) {
            tp += 6;
            size_t te = val.find("\"", tp);
            ts = val.substr(tp, te - tp);
        }
        if (!first) json += ",";
        first = false;
        json += "{\"problemId\":" + std::to_string(pid)
             + ",\"version\":" + std::to_string(ver)
             + ",\"updatedAt\":\"" + ts + "\""
             + ",\"preview\":\"" + jsonEscape(preview) + "\"}";
    }
    json += "]";
    return dup(json);
}

OJ_API void oj_free_string(const char* s) {
    if (s) free((void*)s);
}

} // extern "C"
