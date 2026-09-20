// api.cpp — 出题服务端核心（authorcore.dll）
// 负责：题目管理（建题/题面/样例）、编译标程与 spj、运行标程生成答案、
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
std::string g_root;          // 题库根目录
std::string g_lastError;

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
// meta.json 格式：{"timeLimitMs":1000,"memLimitMB":256,"tags":["基础"],"version":3,"updatedAt":"..."}
struct ProblemMeta {
    long long timeLimitMs = 1000;
    long long memLimitMB = 256;
    std::string tagsJson;   // JSON 数组原文
    long long version = 0;
    std::string updatedAt;
};

std::string nowStr() {
    SYSTEMTIME st;
    GetLocalTime(&st);
    char buf[32];
    snprintf(buf, sizeof(buf), "%04d-%02d-%02d %02d:%02d:%02d",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

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
                     + ",\"version\":" + std::to_string(m.version)
                     + ",\"updatedAt\":\"" + jsonEscape(m.updatedAt) + "\"}";
    writeFile(dir + "\\meta.json", json);
}

// 历史版本列表（数字目录）
std::vector<int> listVersions(const std::string& hisDir) {
    std::vector<int> res;
    std::string pattern = hisDir + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
            std::string name = fd.cFileName;
            if (name == "." || name == "..") continue;
            int v = atoi(name.c_str());
            if (v > 0) res.push_back(v);
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    std::sort(res.begin(), res.end());
    return res;
}

} // namespace

extern "C" {

AC_API int ac_init(const char* problems_dir) {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    g_root = problems_dir ? problems_dir : "";
    CreateDirectoryA(g_root.c_str(), NULL);
    return 0;
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
        int inCnt = (int)listInFiles(dir).size();
        if (i) json += ",";
        json += "{\"id\":" + dirs[i]
             + ",\"title\":\"" + jsonEscape(title) + "\""
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
         + ",\"version\":" + std::to_string(m.version)
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
    std::string dir = problemDir(id);
    std::string json = "{\"ok\":true,\"results\":[";
    bool first = true;
    for (auto& f : listInFiles(dir)) {
        std::string base = f.substr(0, f.size() - 3);
        std::string inFile = dir + "\\" + f;
        std::string outFile = dir + "\\" + base + ".out";
        oj::RunOutcome r = oj::run_one(std_exe ? std_exe : "", inFile, outFile, 2000, 256ull * 1024 * 1024);
        std::string status = "OK";
        if (r.status == 1) status = "TLE";
        else if (r.status == 2) status = "RE";
        else if (r.status == 3) status = "SE";
        if (!first) json += ",";
        first = false;
        json += "{\"file\":\"" + jsonEscape(f) + "\",\"status\":\"" + status
              + "\",\"ms\":" + std::to_string(r.ms) + "}";
    }
    json += "]}";
    return dup(json);
}

// 完整性校验
AC_API const char* ac_validate(int id) {
    std::string dir = problemDir(id);
    std::string json = "{\"ok\":";
    std::vector<std::string> missing;
    if (!exists(dir + "\\statement.txt")) missing.push_back("statement.txt");
    if (!exists(dir + "\\sample.in"))    missing.push_back("sample.in");
    if (!exists(dir + "\\sample.out"))   missing.push_back("sample.out");
    auto ins = listInFiles(dir);
    for (auto& f : ins) {
        std::string base = f.substr(0, f.size() - 3);
        if (!exists(dir + "\\" + base + ".out")) missing.push_back(base + ".out");
    }
    bool hasStd = exists(dir + "\\std.cpp");
    bool hasSpj = exists(dir + "\\spj.cpp");
    json += std::to_string(missing.empty() && !ins.empty());
    json += ",\"inCount\":" + std::to_string(ins.size());
    json += ",\"hasStd\":" + std::string(hasStd ? "true" : "false");
    json += ",\"hasSpj\":" + std::string(hasSpj ? "true" : "false");
    json += ",\"missing\":[";
    for (size_t i = 0; i < missing.size(); ++i) {
        if (i) json += ",";
        json += "\"" + missing[i] + "\"";
    }
    json += "]}";
    return dup(json);
}

// 分发：快照到 history/{v}（版本自增）→ 整体复制 {id} 到 target_root\{id}
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

    // 1. 版本自增 + 写回 meta.json
    ProblemMeta m = readMeta(src);
    long long ver = m.version + 1;
    m.version = ver;
    m.updatedAt = nowStr();
    writeMeta(src, m);

    // 2. 快照：完整复制 {id} 到 {id}\history\{v}（跳过 history 自身防递归）
    std::string hisDir = src + "\\history";
    std::string snap = hisDir + "\\" + std::to_string(ver);
    mkdirs(snap);
    copyDir(src, snap, "history");

    // 3. 复制到目标（跳过 history —— 历史版本仅服务端可见，不下发给客户端）
    CreateDirectoryA(dstRoot.c_str(), NULL);
    std::string dst = dstRoot + "\\" + std::to_string(id);
    // 目标已存在则先清空再复制，保证与题库一致
    if (exists(dst)) {
        std::string cmd = "rmdir /s /q \"" + dst + "\"";
        system(cmd.c_str());
    }
    bool ok = copyDir(src, dst, "history");
    if (!ok) {
        std::string err = "复制到 " + dst + " 失败";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    g_lastError.clear();
    return dup("{\"ok\":true,\"target\":\"" + jsonEscape(dst)
             + "\",\"version\":" + std::to_string(ver) + "}");
}

AC_API const char* ac_get_history(int id) {
    std::string dir = problemDir(id);
    if (!exists(dir)) {
        std::string err = "题目 " + std::to_string(id) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    auto versions = listVersions(dir + "\\history");
    std::string json = "[";
    bool first = true;
    for (auto it = versions.rbegin(); it != versions.rend(); ++it) {  // 新版在前
        int v = *it;
        std::string vdir = dir + "\\history\\" + std::to_string(v);
        std::string st = readFile(vdir + "\\statement.txt");
        size_t nl = st.find('\n');
        std::string title = (nl == std::string::npos) ? st : st.substr(0, nl);
        ProblemMeta m = readMeta(vdir);
        if (!first) json += ",";
        first = false;
        json += "{\"version\":" + std::to_string(v)
             + ",\"title\":\"" + jsonEscape(title) + "\""
             + ",\"timeLimitMs\":" + std::to_string(m.timeLimitMs)
             + ",\"memLimitMB\":" + std::to_string(m.memLimitMB)
             + ",\"tags\":" + (m.tagsJson.empty() ? "[]" : m.tagsJson)
             + ",\"updatedAt\":\"" + jsonEscape(m.updatedAt) + "\"}";
    }
    json += "]";
    return dup(json);
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
    if (exists(dst)) {
        std::string cmd = "rmdir /s /q \"" + dst + "\"";
        system(cmd.c_str());
    }
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
