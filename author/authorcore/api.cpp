// api.cpp — 出题服务端核心（authorcore.dll）
// 核心 API：题目列表/元数据/存储/编译运行/数据生成器管理、录入数据、校验、发布、列出所有数据、生成答案。
//       完整性校验、分发到客户端题目目录。
#include "authorcore.h"
#include "judge.h"

#include <windows.h>

#include <algorithm>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include <cstdio>
#include <cstring>
// ===== 全局变量与工具函数（api.cpp / gen.cpp 共用） =====
std::string g_root;          // 题库根目录（ac_init 后只读，多线程安全）
thread_local std::string g_lastError;  // 最近错误：线程本地，支持多题并发编译/评测

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

// 递归删除目录（不借助 cmd，避免闪出控制台窗口）
void removeTree(const std::string& dir) {
    if (!exists(dir)) return;
    std::string pattern = dir + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE hFind = FindFirstFileA(pattern.c_str(), &fd);
    if (hFind != INVALID_HANDLE_VALUE) {
        do {
            std::string name = fd.cFileName;
            if (name == "." || name == "..") continue;
            std::string full = dir + "\\" + name;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                removeTree(full);
            else {
                SetFileAttributesA(full.c_str(), FILE_ATTRIBUTE_NORMAL);
                DeleteFileA(full.c_str());
            }
        } while (FindNextFileA(hFind, &fd));
        FindClose(hFind);
    }
    RemoveDirectoryA(dir.c_str());
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

const char* dup(const std::string& s) {
    char* p = (char*)malloc(s.size() + 1);
    memcpy(p, s.c_str(), s.size() + 1);
    return p;
}

// 逐级创建目录
void mkdirs(const std::string& path) {
    std::string cur;
    for (size_t i = 0; i < path.size(); ++i) {
        cur += path[i];
        if (path[i] == '\\' || path[i] == '/') {
            CreateDirectoryA(cur.c_str(), NULL);
        }
    }
    CreateDirectoryA(path.c_str(), NULL);
}

namespace {

std::string problemDir(int id) {
    return g_root + "\\" + std::to_string(id);
}

// 枚举目录下所有文件（不含子目录）
std::vector<std::string> listFiles(const std::string& dir) {
    std::vector<std::string> res;
    std::string pattern = dir + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return res;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        res.push_back(fd.cFileName);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    std::sort(res.begin(), res.end());
    return res;
}

// 枚举 *.in（跳过 sample.in）
std::vector<std::string> listInFiles(const std::string& dir) {
    std::vector<std::string> res;
    for (auto& f : listFiles(dir)) {
        if (f.size() < 4) continue;
        if (f.substr(f.size() - 3) != ".in") continue;
        if (f.rfind("sample", 0) == 0) continue;
        res.push_back(f);
    }
    return res;
}

// 递归复制目录 src -> dst；skipSub 非空时跳过同名子目录（防快照递归）
bool copyDir(const std::string& src, const std::string& dst, const std::string& skipSub = "") {
    CreateDirectoryA(dst.c_str(), NULL);
    std::string pattern = src + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return false;
    bool ok = true;
    do {
        std::string name = fd.cFileName;
        if (name == "." || name == "..") continue;
        std::string s = src + "\\" + name;
        std::string d = dst + "\\" + name;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            // 历史版本目录不下发：发布时跳过 history / gen_history
            if (name == "history" || name == "gen_history") continue;
            if (!skipSub.empty() && name == skipSub) continue;
            if (!copyDir(s, d, skipSub)) ok = false;
        } else {
            if (!CopyFileA(s.c_str(), d.c_str(), FALSE)) {
                g_lastError = "CopyFileA failed: " + s + " -> " + d + " (err=" + std::to_string(GetLastError()) + ")";
                ok = false;
            }
        }
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    return ok;
}

// ---- 题目元数据 meta.json ----
// meta.json 格式：{"timeLimitMs":1000,"memLimitMB":256,"tags":["基础"],"updatedAt":"..."}
struct ProblemMeta {
    long long timeLimitMs = 1000;
    long long memLimitMB = 256;
    std::string tagsJson;   // JSON 数组原文
    std::string updatedAt;
};

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

void writeMeta(const std::string& dir, const ProblemMeta& m) {
    std::string json = "{\"timeLimitMs\":" + std::to_string(m.timeLimitMs)
                     + ",\"memLimitMB\":" + std::to_string(m.memLimitMB)
                     + ",\"tags\":" + (m.tagsJson.empty() ? "[]" : m.tagsJson)
                     + ",\"updatedAt\":\"" + jsonEscape(m.updatedAt) + "\"}";
    writeFile(dir + "\\meta.json", json);
}

} // namespace

extern "C" {

AC_API int ac_init(const char* problems_dir) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    g_root = problems_dir ? problems_dir : "";
    CreateDirectoryA(g_root.c_str(), NULL);
    return 0;
}

// enumerate generator sub-dirs under a problem dir (dirs contain gen.cpp)
static std::vector<std::string> listGenDirs(int id) {
    std::vector<std::string> out;
    out.push_back("");  // 题目根目录（手工 .in/.out 同样作为判题数据源）
    std::string root = problemDir(id);
    std::string pat = root + "\\*";
    WIN32_FIND_DATAA fd; HANDLE h = FindFirstFileA(pat.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return out;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            std::string n = fd.cFileName;
            if (n != "." && n != ".." && exists(root + "\\" + n + "\\gen.cpp")) out.push_back(n);
        }
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    return out;
}
static int countGenInputs(int id) {
    int n = 0;
    for (auto& g : listGenDirs(id)) n += (int)listInFiles(problemDir(id) + "\\" + g).size();
    return n;
}

AC_API const char* ac_list(void) {
    std::vector<std::string> dirs;
    std::string pattern = g_root + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                std::string name = fd.cFileName;
                if (name == "." || name == "..") continue;
                int id = atoi(name.c_str());
                if (id > 0) dirs.push_back(name);
            }
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    std::sort(dirs.begin(), dirs.end(),
              [](const std::string& a, const std::string& b) { return atoi(a.c_str()) < atoi(b.c_str()); });

    std::string json = "[";
    for (size_t i = 0; i < dirs.size(); ++i) {
        std::string dir = g_root + "\\" + dirs[i];
        std::string st = readFile(dir + "\\statement.txt");
        size_t nl = st.find('\n');
        std::string title = (nl == std::string::npos) ? st : st.substr(0, nl);
        int genCnt = (int)listGenDirs(atoi(dirs[i].c_str())).size();
        int inCnt = countGenInputs(atoi(dirs[i].c_str()));
        if (i) json += ",";
        json += "{\"id\":" + dirs[i]
             + ",\"title\":\"" + jsonEscape(title) + "\""
             + ",\"genCount\":" + std::to_string(genCnt)
             + ",\"dataCount\":" + std::to_string(inCnt) + "}";
    }
    json += "]";
    return dup(json);
}

AC_API const char* ac_create(int id, const char* title, const char* desc,
                             const char* sample_in, const char* sample_out) {
    std::string dir = problemDir(id);
    if (exists(dir)) {
        std::string err = "题目 " + std::to_string(id) + " 已存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    CreateDirectoryA(dir.c_str(), NULL);
    std::string st = std::string(title ? title : "") + "\n\n" + (desc ? desc : "");
    writeFile(dir + "\\statement.txt", st);
    writeFile(dir + "\\sample.in", sample_in ? sample_in : "");
    writeFile(dir + "\\sample.out", sample_out ? sample_out : "");
    g_lastError.clear();
    return dup("{\"ok\":true}");
}

AC_API const char* ac_save_statement(int id, const char* title, const char* desc,
                                     const char* sample_in, const char* sample_out) {
    std::string dir = problemDir(id);
    if (!exists(dir)) {
        std::string err = "题目 " + std::to_string(id) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string st = std::string(title ? title : "") + "\n\n" + (desc ? desc : "");
    writeFile(dir + "\\statement.txt", st);
    writeFile(dir + "\\sample.in", sample_in ? sample_in : "");
    writeFile(dir + "\\sample.out", sample_out ? sample_out : "");
    g_lastError.clear();
    return dup("{\"ok\":true}");
}

AC_API const char* ac_save_meta(int id, int time_ms, int mem_mb, const char* tags_json) {
    std::string dir = problemDir(id);
    if (!exists(dir)) {
        std::string err = "题目 " + std::to_string(id) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    if (time_ms <= 0) time_ms = 1000;
    if (mem_mb <= 0) mem_mb = 256;
    ProblemMeta m = readMeta(dir);
    m.timeLimitMs = time_ms;
    m.memLimitMB = mem_mb;
    m.tagsJson = (tags_json && *tags_json) ? tags_json : "[]";
    writeMeta(dir, m);
    g_lastError.clear();
    return dup("{\"ok\":true}");
}

AC_API const char* ac_get_meta(int id) {
    std::string dir = problemDir(id);
    if (!exists(dir)) {
        std::string err = "题目 " + std::to_string(id) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    ProblemMeta m = readMeta(dir);
    std::string json = "{\"ok\":true";
    json += ",\"timeLimitMs\":" + std::to_string(m.timeLimitMs)
         + ",\"memLimitMB\":" + std::to_string(m.memLimitMB)
         + ",\"tags\":" + (m.tagsJson.empty() ? "[]" : m.tagsJson)
         + ",\"updatedAt\":\"" + jsonEscape(m.updatedAt) + "\"}";
    return dup(json);
}

AC_API const char* ac_compile(const char* src_file, const char* exe_file) {
    // 确保输出 exe 所在目录存在（否则 g++ 无法创建输出文件）
    std::string exe = exe_file ? exe_file : "";
    size_t pos = exe.find_last_of("\\/");
    if (pos != std::string::npos) {
        mkdirs(exe.substr(0, pos));
    }
    std::string err;
    bool ok = oj::compile_cpp(src_file ? src_file : "", exe, err);
    g_lastError = ok ? "" : err;
    if (ok) return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

// 运行标程 std_exe 对题目所有 *.in 生成 *.out
AC_API const char* ac_gen_outputs(int id, const char* std_exe) {
    std::string json = "{\"ok\":true,\"results\":[";
    bool first = true;
    for (auto& g : listGenDirs(id)) {
        std::string dir = problemDir(id) + "\\" + g;
        for (auto& f : listInFiles(dir)) {
            std::string base = f.substr(0, f.size() - 3);
            std::string inFile = dir + "\\" + f;
            std::string outFile = dir + "\\" + base + ".out";
            oj::RunOutcome r = oj::run_one(std_exe ? std_exe : "", inFile, outFile, 2000, 256ull * 1024 * 1024);
            std::string status = "OK";
            if (r.status == 1) status = "TLE";
            else if (r.status == 2) { const char* d = oj::crash_desc(r.exitCode); status = d ? std::string("RE:") + d : "RE"; }
            else if (r.status == 3) status = "SE";
            if (!first) json += ",";
            first = false;
            json += "{\"file\":\"" + jsonEscape(g + "\\" + f) + "\",\"status\":\"" + status
                  + "\",\"ms\":" + std::to_string(r.ms) + "}";
        }
    }
    json += "]}";
    g_lastError.clear();
    return dup(json);
}

// 完整性校验：题面检查（标题/描述/样例/标程）+ 生成器检查（.in/.out）
AC_API const char* ac_validate(int id) {
    std::string dir = problemDir(id);

    // —— 题面检查 ——
    std::string st = readFile(dir + "\\statement.txt");
    size_t nl = st.find('\n');
    std::string title = (nl == std::string::npos) ? st : st.substr(0, nl);
    std::string desc  = (nl == std::string::npos) ? "" : st.substr(nl + 1);
    auto trim = [](std::string s) {
        size_t a = s.find_first_not_of(" \t\r\n");
        if (a == std::string::npos) return std::string();
        size_t b = s.find_last_not_of(" \t\r\n");
        return s.substr(a, b - a + 1);
    };
    bool titleOk = !trim(title).empty();
    bool descOk  = !trim(desc).empty();
    bool hasStd = exists(dir + "\\std.cpp");

    // —— 生成器检查 ——
    auto gens = listGenDirs(id);
    int genCount = (int)gens.size();
    int inCount = 0;
    std::vector<std::string> missingOut;
    for (auto& g : gens) {
        std::string gd = dir + "\\" + g;
        for (auto& fn : listInFiles(gd)) {
            ++inCount;
            std::string base = fn.substr(0, fn.size() - 3);
            if (!exists(gd + "\\" + base + ".out")) missingOut.push_back(g + "\\" + base + ".out");
        }
    }

    std::vector<std::string> missing;
    if (!exists(dir + "\\statement.txt")) missing.push_back("statement.txt");

    bool ok = titleOk && descOk && hasStd
           && genCount > 0 && inCount > 0 && missingOut.empty();

    std::string json = "{\"ok\":" + std::string(ok ? "true" : "false");
    json += ",\"title\":" + std::string(titleOk ? "true" : "false");
    json += ",\"desc\":" + std::string(descOk ? "true" : "false");
    json += ",\"std\":" + std::string(hasStd ? "true" : "false");
    json += ",\"genCount\":" + std::to_string(genCount);
    json += ",\"inCount\":" + std::to_string(inCount);
    json += ",\"missing\":[";
    for (size_t i = 0; i < missing.size(); ++i) {
        if (i) json += ",";
        json += "\"" + jsonEscape(missing[i]) + "\"";
    }
    json += "],\"missingOut\":[";
    for (size_t i = 0; i < missingOut.size(); ++i) {
        if (i) json += ",";
        json += "\"" + jsonEscape(missingOut[i]) + "\"";
    }
    json += "]}";
    g_lastError.clear();
    return dup(json);
}

// 分发：整体复制 {id} 到 target_root\{id}（history / gen_history 历史目录不下发）
AC_API const char* ac_publish(int id, const char* target_root) {
    std::string src = problemDir(id);
    if (!exists(src)) {
        std::string err = "题目 " + std::to_string(id) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string dstRoot = target_root ? target_root : "";
    if (dstRoot.empty()) {
        std::string err = "目标目录为空";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }

    CreateDirectoryA(dstRoot.c_str(), NULL);
    std::string dst = dstRoot + "\\" + std::to_string(id);
    // 目标已存在则先清空再复制，保证与题库一致
    removeTree(dst);
    bool ok = copyDir(src, dst);
    if (!ok) {
        std::string err = "复制到 " + dst + " 失败";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    g_lastError.clear();
    return dup("{\"ok\":true,\"target\":\"" + jsonEscape(dst) + "\"}");
}

AC_API const char* ac_contest_create(int cid, const char* name, const char* desc,
                                     const char* start_time, const char* end_time,
                                     const char* problems_json) {
    if (cid <= 0) {
        std::string err = "比赛编号必须是正整数";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string dir = g_root + "\\contests\\" + std::to_string(cid);
    mkdirs(dir);
    std::string json = "{\"id\":" + std::to_string(cid)
        + ",\"name\":\"" + jsonEscape(name ? name : "") + "\""
        + ",\"description\":\"" + jsonEscape(desc ? desc : "") + "\""
        + ",\"startTime\":\"" + jsonEscape(start_time ? start_time : "") + "\""
        + ",\"endTime\":\"" + jsonEscape(end_time ? end_time : "") + "\""
        + ",\"problems\":" + (problems_json && *problems_json ? problems_json : "[]") + "}";
    writeFile(dir + "\\contest.json", json);
    g_lastError.clear();
    return dup("{\"ok\":true}");
}

AC_API const char* ac_contest_list(void) {
    std::vector<int> ids;
    std::string pattern = g_root + "\\contests\\*";
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
        std::string raw = readFile(g_root + "\\contests\\" + std::to_string(cid) + "\\contest.json");
        std::string name, startT, endT;
        auto findVal = [&](const char* key, std::string& out) {
            std::string k = std::string("\"") + key + "\"";
            size_t p = raw.find(k);
            if (p == std::string::npos) return;
            p = raw.find(':', p + k.size());
            if (p == std::string::npos) return;
            p++;
            while (p < raw.size() && (raw[p] == ' ' || raw[p] == '\t')) p++;
            size_t e = raw.find('"', p + 1);
            if (p < raw.size() && raw[p] == '"' && e != std::string::npos) out = raw.substr(p + 1, e - p - 1);
        };
        findVal("name", name);
        findVal("startTime", startT);
        findVal("endTime", endT);
        // problems 数量
        size_t pp = raw.find("\"problems\"");
        int pc = 0;
        if (pp != std::string::npos) {
            pp = raw.find('[', pp);
            size_t pe = raw.find(']', pp);
            if (pp != std::string::npos && pe != std::string::npos) {
                std::string arr = raw.substr(pp + 1, pe - pp - 1);
                pc = arr.empty() ? 0 : (int)(std::count(arr.begin(), arr.end(), ',') + 1);
            }
        }
        if (!first) json += ",";
        first = false;
        json += "{\"id\":" + std::to_string(cid)
             + ",\"name\":\"" + jsonEscape(name) + "\""
             + ",\"problemCount\":" + std::to_string(pc)
             + ",\"startTime\":\"" + jsonEscape(startT) + "\""
             + ",\"endTime\":\"" + jsonEscape(endT) + "\"}";
    }
    json += "]";
    return dup(json);
}

AC_API const char* ac_contest_get(int cid) {
    std::string path = g_root + "\\contests\\" + std::to_string(cid) + "\\contest.json";
    if (!exists(path)) {
        std::string err = "比赛 " + std::to_string(cid) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string raw = readFile(path);
    // 转成带 ok 字段的响应
    std::string json = raw;
    if (!json.empty() && json[0] == '{') json = "{\"ok\":true," + json.substr(1);
    else json = "{\"ok\":false,\"error\":\"配置损坏\"}";
    return dup(json);
}

AC_API const char* ac_contest_publish(int cid, const char* target_root) {
    std::string src = g_root + "\\contests\\" + std::to_string(cid);
    if (!exists(src + "\\contest.json")) {
        std::string err = "比赛 " + std::to_string(cid) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string dstRoot = target_root ? target_root : "";
    if (dstRoot.empty()) {
        std::string err = "目标目录为空";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    CreateDirectoryA(dstRoot.c_str(), NULL);
    std::string dst = dstRoot + "\\contests\\" + std::to_string(cid);
    removeTree(dst);
    bool ok = copyDir(src, dst);
    if (!ok) {
        std::string err = "复制到 " + dst + " 失败";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    g_lastError.clear();
    return dup("{\"ok\":true,\"target\":\"" + jsonEscape(dst) + "\"}");
}

AC_API const char* ac_last_error(void) { return dup(g_lastError); }

AC_API void ac_free_string(const char* s) { if (s) free((void*)s); }

} // extern "C"
