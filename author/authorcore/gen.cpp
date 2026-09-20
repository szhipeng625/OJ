// gen.cpp — 数据生成器管理（保存/版本/编译/运行/查找/文件查看）
// 全部存储操作在 C++ 端完成，WPF 前端只调用导出函数并展示结果。
#include "authorcore.h"
#include "judge.h"

#include <windows.h>

#include <algorithm>
#include <cctype>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include <cstdio>
#include <cstring>
// 来自 api.cpp 的全局变量
extern std::string g_root;
extern std::string g_lastError;

// 来自 api.cpp 的全局工具函数
std::string readFile(const std::string& path);
void writeFile(const std::string& path, const std::string& content);
bool exists(const std::string& path);
std::string jsonEscape(const std::string& s);
const char* dup(const std::string& s);
void mkdirs(const std::string& path);

namespace {

std::string g_temp;

std::string genProblemDir(int id) { return g_root + "\\" + std::to_string(id); }
// 生成器源码按题号命名：{题库}\{pid}\{pid}.cpp（不再统一叫 gen.cpp）
std::string genSrcPath(int id) { return genProblemDir(id) + "\\" + std::to_string(id) + ".cpp"; }
std::string genHistoryDir(int id) { return genProblemDir(id) + "\\gen_history"; }
std::string genExePath(int id) { return g_temp + "\\" + std::to_string(id) + "\\" + std::to_string(id) + ".exe"; }

// generated 根目录：题库父目录下的 generated 目录
std::string generatedRoot() {
    std::string r = g_root;
    size_t p = r.find_last_of("\\/");
    if (p != std::string::npos) r = r.substr(0, p);
    return r + "\\generated";
}
std::string generatedDir(int id) { return generatedRoot() + "\\" + std::to_string(id); }

const char* kGenTemplate =
    "// 数据生成器 {题号}.cpp\n"
    "// 运行约定：本程序 <编号/种子>，把测试数据写到标准输出 stdout；\n"
    "// 服务端点「生成数据」时会把 stdout 重定向保存为 generated/<题号>/<编号>.in\n"
    "#include <bits/stdc++.h>\n"
    "using namespace std;\n\n"
    "int main(int argc, char** argv) {\n"
    "    int seed = (argc > 1) ? atoi(argv[1]) : 1;\n"
    "    mt19937 rng((unsigned)seed);\n"
    "    uniform_int_distribution<int> dist(1, 100);\n\n"
    "    int n = dist(rng);\n"
    "    printf(\"%d\\n\", n);\n"
    "    for (int i = 0; i < n; ++i)\n"
    "        printf(\"%d%c\", dist(rng), i + 1 == n ? '\\n' : ' ');\n"
    "    return 0;\n"
    "}\n";

std::string nowStamp() {
    SYSTEMTIME st; GetLocalTime(&st);
    char buf[32];
    snprintf(buf, sizeof(buf), "%04d%02d%02d_%02d%02d%02d",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

std::string nowDisplay() {
    SYSTEMTIME st; GetLocalTime(&st);
    char buf[32];
    snprintf(buf, sizeof(buf), "%04d-%02d-%02d %02d:%02d:%02d",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
    return buf;
}

std::string fileModifiedDisplay(const std::string& path) {
    WIN32_FILE_ATTRIBUTE_DATA fad;
    if (!GetFileAttributesExA(path.c_str(), GetFileExInfoStandard, &fad)) return "";
    FILETIME lt; FileTimeToLocalFileTime(&fad.ftLastWriteTime, &lt);
    SYSTEMTIME st; FileTimeToSystemTime(&lt, &st);
    char buf[32];
    snprintf(buf, sizeof(buf), "%02d-%02d %02d:%02d", st.wMonth, st.wDay, st.wHour, st.wMinute);
    return buf;
}

long long fileSize(const std::string& path) {
    WIN32_FILE_ATTRIBUTE_DATA fad;
    if (!GetFileAttributesExA(path.c_str(), GetFileExInfoStandard, &fad)) return 0;
    LARGE_INTEGER li; li.HighPart = fad.nFileSizeHigh; li.LowPart = fad.nFileSizeLow;
    return li.QuadPart;
}

int countLines(const std::string& s) {
    if (s.empty()) return 0;
    int n = 1;
    for (char c : s) if (c == '\n') ++n;
    return n;
}

std::string codeSummary(const std::string& s) {
    std::istringstream iss(s);
    std::string line;
    while (std::getline(iss, line)) {
        std::string t = line;
        // trim
        size_t a = t.find_first_not_of(" \t\r\n");
        if (a == std::string::npos) continue;
        t = t.substr(a);
        if (t.rfind("//", 0) == 0) continue;
        if (t.size() > 60) t = t.substr(0, 60) + "…";
        return t;
    }
    return "";
}

std::vector<std::string> listGenHistory(int id) {
    std::vector<std::string> res;
    std::string dir = genHistoryDir(id);
    std::string pattern = dir + "\\v*.cpp";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return res;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        res.push_back(fd.cFileName);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    return res;
}

int parseVersion(const std::string& fileName) {
    // vN_yyyyMMdd_HHmmss.cpp 或 vN.cpp
    if (fileName.size() < 2 || fileName[0] != 'v') return 0;
    int v = 0;
    for (size_t i = 1; i < fileName.size() && isdigit((unsigned char)fileName[i]); ++i)
        v = v * 10 + (fileName[i] - '0');
    return v;
}

std::string parseStamp(const std::string& fileName) {
    // vN_stamp.cpp → stamp
    size_t us = fileName.find('_');
    size_t dot = fileName.rfind('.');
    if (us == std::string::npos || dot == std::string::npos || us >= dot) return "";
    return fileName.substr(us + 1, dot - us - 1);
}

std::string stampToDisplay(const std::string& stamp) {
    // yyyyMMdd_HHmmss → yyyy-MM-dd HH:mm:ss
    if (stamp.size() != 15) return stamp;
    return stamp.substr(0, 4) + "-" + stamp.substr(4, 2) + "-" + stamp.substr(6, 2)
         + " " + stamp.substr(9, 2) + ":" + stamp.substr(11, 2) + ":" + stamp.substr(13, 2);
}

// 运行子进程并捕获 stdout（合并 stderr），超时毫秒
struct RunCap {
    bool ok = false;
    bool timeout = false;
    int exitCode = 0;
    std::string output;
};

RunCap runCapture(const std::string& exe, const std::string& args,
                   const std::string& workDir, int timeoutMs) {
    RunCap r;
    SECURITY_ATTRIBUTES sa{sizeof(sa), NULL, TRUE};
    HANDLE hOutRead = NULL, hOutWrite = NULL;
    if (!CreatePipe(&hOutRead, &hOutWrite, &sa, 0)) return r;
    SetHandleInformation(hOutRead, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOA si{sizeof(si)};
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = hOutWrite;
    si.hStdError = hOutWrite;
    PROCESS_INFORMATION pi;
    std::string cmd = "\"" + exe + "\" " + args;
    std::vector<char> cmdBuf(cmd.begin(), cmd.end());
    cmdBuf.push_back('\0');
    if (!CreateProcessA(NULL, cmdBuf.data(), NULL, NULL, TRUE, 0, NULL,
                         workDir.empty() ? NULL : workDir.c_str(), &si, &pi)) {
        CloseHandle(hOutRead); CloseHandle(hOutWrite);
        return r;
    }
    CloseHandle(hOutWrite);
    std::string out;
    char buf[8192];
    DWORD dwRead;
    while (ReadFile(hOutRead, buf, sizeof(buf), &dwRead, NULL) && dwRead > 0)
        out.append(buf, dwRead);
    CloseHandle(hOutRead);
    DWORD wait = WaitForSingleObject(pi.hProcess, timeoutMs);
    if (wait == WAIT_TIMEOUT) { TerminateProcess(pi.hProcess, 1); r.timeout = true; }
    GetExitCodeProcess(pi.hProcess, (LPDWORD)&r.exitCode);
    CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
    r.ok = !r.timeout && r.exitCode == 0;
    r.output = out;
    return r;
}

std::string toLower(const std::string& s) {
    std::string r = s;
    for (char& c : r) c = (char)tolower((unsigned char)c);
    return r;
}

} // namespace

extern "C" {

AC_API void ac_gen_set_temp(const char* temp_dir) {
    g_temp = temp_dir ? temp_dir : "";
    if (!g_temp.empty()) mkdirs(g_temp);
}

// 读取当前 gen.cpp（不存在则返回模板），同时返回路径信息
AC_API const char* ac_gen_get_current(int id) {
    std::string src = genSrcPath(id);
    bool has = exists(src);
    std::string code = has ? readFile(src) : std::string(kGenTemplate);
    std::string json = "{\"ok\":true"
        ",\"hasGen\":" + std::string(has ? "true" : "false") +
        ",\"code\":\"" + jsonEscape(code) + "\"" +
        ",\"genPath\":\"" + jsonEscape(src) + "\"" +
        ",\"outDir\":\"" + jsonEscape(generatedDir(id)) + "\"}";
    return dup(json);
}

// 保存 gen.cpp 并归档版本快照
AC_API const char* ac_gen_save(int id, const char* code) {
    std::string dir = genProblemDir(id);
    if (!exists(dir)) {
        std::string err = "题目 " + std::to_string(id) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string src = genSrcPath(id);
    writeFile(src, code ? code : "");

    std::string hdir = genHistoryDir(id);
    mkdirs(hdir);
    int next = 1;
    for (auto& f : listGenHistory(id)) {
        int v = parseVersion(f);
        if (v >= next) next = v + 1;
    }
    std::string snapName = "v" + std::to_string(next) + "_" + nowStamp() + ".cpp";
    std::string snap = hdir + "\\" + snapName;
    if (!CopyFileA(src.c_str(), snap.c_str(), FALSE)) {
        std::string err = "归档版本失败 (err=" + std::to_string(GetLastError()) + ")";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    g_lastError.clear();
    return dup("{\"ok\":true,\"version\":" + std::to_string(next)
             + ",\"snapshot\":\"" + jsonEscape(snapName) + "\"}");
}

// 版本列表（新版在前）
AC_API const char* ac_gen_list_versions(int id) {
    std::vector<std::string> files = listGenHistory(id);
    std::sort(files.begin(), files.end(),
              [](const std::string& a, const std::string& b) {
                  return parseVersion(a) > parseVersion(b);
              });
    std::string json = "[";
    bool first = true;
    for (auto& f : files) {
        int v = parseVersion(f);
        std::string stamp = parseStamp(f);
        std::string text = readFile(genHistoryDir(id) + "\\" + f);
        int lines = countLines(text);
        std::string summary = codeSummary(text);
        if (!first) json += ",";
        first = false;
        json += "{\"version\":" + std::to_string(v)
             + ",\"stamp\":\"" + jsonEscape(stamp) + "\""
             + ",\"fileName\":\"" + jsonEscape(f) + "\""
             + ",\"lines\":" + std::to_string(lines)
             + ",\"summary\":\"" + jsonEscape(summary) + "\""
             + ",\"time\":\"" + jsonEscape(stampToDisplay(stamp)) + "\"}";
    }
    json += "]";
    return dup(json);
}

// 读取某个版本的代码内容
AC_API const char* ac_gen_get_version(int id, int version) {
    std::string target;
    for (auto& f : listGenHistory(id)) {
        if (parseVersion(f) == version) { target = f; break; }
    }
    if (target.empty()) {
        std::string err = "版本 v" + std::to_string(version) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string code = readFile(genHistoryDir(id) + "\\" + target);
    g_lastError.clear();
    return dup("{\"ok\":true,\"code\":\"" + jsonEscape(code) + "\"}");
}

// 编译已保存的 gen.cpp → temp\{id}\gen.exe
AC_API const char* ac_gen_compile(int id) {
    std::string src = genSrcPath(id);
    if (!exists(src)) {
        std::string err = "请先保存生成器代码";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string exe = genExePath(id);
    mkdirs(g_temp + "\\" + std::to_string(id));
    std::string err;
    bool ok = oj::compile_cpp(src, exe, err);
    g_lastError = ok ? "" : err;
    if (ok) return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

// 运行 gen.exe，gen.exe 接受输出目录作为 argv[1]，自行生成 n 个 .in 文件写入该目录
AC_API const char* ac_gen_run(int id) {
    std::string exe = genExePath(id);
    if (!exists(exe)) {
        std::string src = genSrcPath(id);
        if (!exists(src)) {
            std::string err = "请先保存并编译生成器代码";
            g_lastError = err;
            return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
        }
        mkdirs(g_temp + "\\" + std::to_string(id));
        std::string cerr;
        if (!oj::compile_cpp(src, exe, cerr)) {
            g_lastError = cerr;
            return dup("{\"ok\":false,\"error\":\"生成器编译失败：" + jsonEscape(cerr) + "\"}");
        }
    }
    std::string outDir = generatedDir(id);
    mkdirs(outDir);
    RunCap r = runCapture(exe, outDir, outDir, 60000);
    if (r.timeout) {
        g_lastError = "生成器运行超时（60s）";
        return dup("{\"ok\":false,\"error\":\"生成器运行超时（60s）\"}");
    }
    if (!r.ok) {
        std::string err = "生成器退出码" + std::to_string(r.exitCode);
        if (!r.output.empty()) err += ": " + r.output.substr(0, 200);
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    int cnt = 0;
    std::string pattern = outDir + "\\*.in";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            ++cnt;
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    std::string json = "{\"ok\":true,\"generated\":" + std::to_string(cnt)
        + ",\"total\":" + std::to_string(cnt)
        + ",\"outDir\":\"" + jsonEscape(outDir) + "\",\"fails\":[]}";
    g_lastError.clear();
    return dup(json);
}
// 生成的数据文件列表
AC_API const char* ac_gen_list_files(int id) {
    std::string dir = generatedDir(id);
    std::vector<std::pair<int, std::string>> items; // (编号, 文件名)
    std::string pattern = dir + "\\*.in";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            std::string name = fd.cFileName;
            std::string base = name.substr(0, name.size() - 3);
            int num = atoi(base.c_str());
            items.push_back({num, name});
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    std::sort(items.begin(), items.end(),
              [](const auto& a, const auto& b) { return a.first < b.first; });
    std::string json = "[";
    bool first = true;
    for (auto& it : items) {
        std::string path = dir + "\\" + it.second;
        if (!first) json += ",";
        first = false;
        json += "{\"name\":\"" + jsonEscape(it.second) + "\""
             + ",\"size\":" + std::to_string(fileSize(path))
             + ",\"modified\":\"" + jsonEscape(fileModifiedDisplay(path)) + "\"}";
    }
    json += "]";
    return dup(json);
}

// 读取某个生成的数据文件内容（限 200KB）
AC_API const char* ac_gen_get_file(int id, const char* name) {
    if (!name || !*name) {
        std::string err = "文件名为空";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string path = generatedDir(id) + "\\" + name;
    if (!exists(path)) {
        std::string err = "文件不存在: " + std::string(name);
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string text = readFile(path);
    bool truncated = false;
    if (text.size() > 200000) { text = text.substr(0, 200000); truncated = true; }
    g_lastError.clear();
    return dup("{\"ok\":true,\"content\":\"" + jsonEscape(text) + "\""
             + ",\"truncated\":" + std::string(truncated ? "true" : "false") + "}");
}

// 跨题查找生成器代码（大小写不敏感，每题最多 30 条）
AC_API const char* ac_gen_search(const char* keyword) {
    if (!keyword || !*keyword) {
        std::string err = "关键字为空";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string kw = toLower(keyword);
    std::string json = "[";
    bool first = true;
    // 遍历题库下所有数字目录
    std::string pattern = g_root + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    std::vector<std::string> dirs;
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
            std::string name = fd.cFileName;
            if (name == "." || name == "..") continue;
            if (atoi(name.c_str()) > 0) dirs.push_back(name);
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    std::sort(dirs.begin(), dirs.end(),
              [](const std::string& a, const std::string& b) { return atoi(a.c_str()) < atoi(b.c_str()); });
    for (auto& d : dirs) {
        std::string genPath = g_root + "\\" + d + "\\" + d + ".cpp";
        if (!exists(genPath)) continue;
        std::string st = readFile(g_root + "\\" + d + "\\statement.txt");
        size_t nl = st.find('\n');
        std::string title = (nl == std::string::npos) ? st : st.substr(0, nl);
        std::string content = readFile(genPath);
        std::istringstream iss(content);
        std::string line;
        int lineNo = 0;
        int n = 0;
        while (std::getline(iss, line) && n < 30) {
            ++lineNo;
            if (toLower(line).find(kw) != std::string::npos) {
                std::string trimmed = line;
                size_t a = trimmed.find_first_not_of(" \t\r\n");
                if (a != std::string::npos) trimmed = trimmed.substr(a);
                size_t b = trimmed.find_last_not_of(" \t\r\n");
                if (b != std::string::npos) trimmed = trimmed.substr(0, b + 1);
                if (!first) json += ",";
                first = false;
                json += "{\"pid\":" + d
                     + ",\"title\":\"" + jsonEscape(title) + "\""
                     + ",\"lineNo\":" + std::to_string(lineNo)
                     + ",\"line\":\"" + jsonEscape(trimmed) + "\""
                     + ",\"keyword\":\"" + jsonEscape(keyword) + "\"}";
                ++n;
            }
        }
    }
    json += "]";
    g_lastError.clear();
    return dup(json);
}

// 把生成的 .in 文件导入到题目测试数据目录
AC_API const char* ac_gen_import_to_problem(int id, const char* name) {
    if (!name || !*name) {
        std::string err = "文件名为空";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string src = generatedDir(id) + "\\" + name;
    if (!exists(src)) {
        std::string err = "文件不存在: " + std::string(name);
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string dst = genProblemDir(id) + "\\" + name;
    if (!CopyFileA(src.c_str(), dst.c_str(), FALSE)) {
        std::string err = "复制失败 (err=" + std::to_string(GetLastError()) + ")";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    g_lastError.clear();
    return dup("{\"ok\":true}");
}

} // extern "C"
