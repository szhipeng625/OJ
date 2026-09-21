// gen.cpp — 数据生成器管理（多生成器：每个生成器一个子目录）
// 一个题目可有多个数据生成器，每个生成器独立目录：{题库}/{id}/{genName}/
//   gen.cpp       生成器代码
//   desc.txt      自定义描述（如"菊花图生成器"）
//   *.in / *.out  生成的数据与标程答案（= 判题数据源）
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
extern thread_local std::string g_lastError;  // api.cpp 定义，线程本地

// 来自 api.cpp 的全局工具函数
std::string readFile(const std::string& path);
void writeFile(const std::string& path, const std::string& content);
bool exists(const std::string& path);
std::string jsonEscape(const std::string& s);
const char* dup(const std::string& s);
void mkdirs(const std::string& path);

namespace {

std::string g_temp;

std::string genProblemRoot(int id) { return g_root + "\\" + std::to_string(id); }
// 生成器子目录
bool validGenName(const std::string& name) {
    if (name.empty() || name == "." || name == "..") return false;
    for (char c : name) {
        if (!isalnum((unsigned char)c) && c != '_' && c != '-' && c != '.') return false;
    }
    // 不允许个别保留目录名（防止与系统目录冲突）
    if (name == "history" || name == "gen_history" || name == "temp") return false;
    return true;
}
std::string genDir(int id, const std::string& name) { return genProblemRoot(id) + "\\" + name; }
std::string genSrcPath(int id, const std::string& name) { return genDir(id, name) + "\\gen.cpp"; }
std::string genDescPath(int id, const std::string& name) { return genDir(id, name) + "\\desc.txt"; }
std::string genExePath(int id, const std::string& name) {
    return g_temp + "\\" + std::to_string(id) + "\\" + name + ".exe";
}

// 数据输出目录 = 生成器目录本身（即判题数据源）
std::string genOutDir(int id, const std::string& name) { return genDir(id, name); }

const char* kGenTemplate =
    "// 数据生成器 {name}.cpp\n"
    "// 运行约定：每个测试点独立运行一次，直接把测试数据输出到 stdout（cout）。\n"
    "// 后台会把 stdout 重定向写入 1.in ~ n.in。\n"
    "// argv[1]=输出目录, argv[2]=随机种子, argv[3]=总组数 n, argv[4]=当前组号 i（1 起）。\n"
    "#include <bits/stdc++.h>\n"
    "using namespace std;\n"
    "int main(int argc, char** argv) {\n"
    "    int seed = (argc > 2) ? atoi(argv[2]) : 1;\n"
    "    mt19937 rng((unsigned)seed);\n"
    "    uniform_int_distribution<int> dist(1, 100);\n"
    "    int m = dist(rng);\n"
    "    cout << m << \"\\n\";\n"
    "    for (int j = 0; j < m; ++j) cout << (j ? \" \" : \"\") << dist(rng);\n"
    "    cout << \"\\n\";\n"
    "    return 0;\n"
    "}\n";

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

// 运行子进程并把 stdout 重定向到指定文件（stderr 捕获为错误文本），超时毫秒
struct RunRedirect {
    bool ok = false;
    bool timeout = false;
    int exitCode = 0;
    std::string errText;
};

RunRedirect runRedirect(const std::string& exe, const std::string& args,
                        const std::string& workDir, const std::string& outFile,
                        int timeoutMs) {
    RunRedirect r;
    SECURITY_ATTRIBUTES sa{sizeof(sa), NULL, TRUE};
    HANDLE hErrRead = NULL, hErrWrite = NULL;
    if (!CreatePipe(&hErrRead, &hErrWrite, &sa, 0)) return r;
    SetHandleInformation(hErrRead, HANDLE_FLAG_INHERIT, 0);
    HANDLE hOut = CreateFileA(outFile.c_str(), GENERIC_WRITE,
                              FILE_SHARE_READ | FILE_SHARE_WRITE, &sa,
                              CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hOut == INVALID_HANDLE_VALUE) {
        CloseHandle(hErrRead); CloseHandle(hErrWrite);
        r.errText = "无法创建输出文件: " + outFile;
        return r;
    }
    STARTUPINFOA si{sizeof(si)};
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = NULL;
    si.hStdOutput = hOut;
    si.hStdError = hErrWrite;
    PROCESS_INFORMATION pi;
    std::string cmd = "\"" + exe + "\" " + args;
    std::vector<char> cmdBuf(cmd.begin(), cmd.end());
    cmdBuf.push_back('\0');
    if (!CreateProcessA(NULL, cmdBuf.data(), NULL, NULL, TRUE, 0, NULL,
                         workDir.empty() ? NULL : workDir.c_str(), &si, &pi)) {
        CloseHandle(hOut); CloseHandle(hErrRead); CloseHandle(hErrWrite);
        r.errText = "启动失败";
        return r;
    }
    CloseHandle(hOut); CloseHandle(hErrWrite);
    char buf[8192];
    DWORD dwRead;
    while (ReadFile(hErrRead, buf, sizeof(buf), &dwRead, NULL) && dwRead > 0)
        r.errText.append(buf, dwRead);
    CloseHandle(hErrRead);
    DWORD wait = WaitForSingleObject(pi.hProcess, timeoutMs);
    if (wait == WAIT_TIMEOUT) { TerminateProcess(pi.hProcess, 1); r.timeout = true; }
    GetExitCodeProcess(pi.hProcess, (LPDWORD)&r.exitCode);
    CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
    r.ok = !r.timeout && r.exitCode == 0;
    return r;
}

std::string toLower(const std::string& s) {
    std::string r = s;
    for (char& c : r) c = (char)tolower((unsigned char)c);
    return r;
}

// 枚举题目下所有生成器子目录名（按名字排序）
std::vector<std::string> listGenerators(int id) {
    std::vector<std::string> res;
    std::string root = genProblemRoot(id);
    std::string pattern = root + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return res;
    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        std::string name = fd.cFileName;
        if (validGenName(name) && exists(genSrcPath(id, name))) res.push_back(name);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    std::sort(res.begin(), res.end());
    return res;
}

// 读取生成器文件数（该生成器目录下 .in 数量）
int genFileCount(int id, const std::string& name) {
    std::string dir = genDir(id, name);
    std::string pattern = dir + "\\*.in";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    int n = 0;
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            ++n;
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    return n;
}

} // namespace

extern "C" {

AC_API void ac_gen_set_temp(const char* temp_dir) {
    g_temp = temp_dir ? temp_dir : "";
    if (!g_temp.empty()) mkdirs(g_temp);
}

// 列出该题全部数据生成器：[{"name":"juhua","desc":"菊花图生成器","fileCount":N}]
AC_API const char* ac_gen_list(int id) {
    auto names = listGenerators(id);
    std::string json = "[";
    for (size_t i = 0; i < names.size(); ++i) {
        std::string desc = readFile(genDescPath(id, names[i]));
        if (i) json += ",";
        json += "{\"name\":\"" + jsonEscape(names[i]) + "\""
             + ",\"desc\":\"" + jsonEscape(desc) + "\""
             + ",\"fileCount\":" + std::to_string(genFileCount(id, names[i])) + "}";
    }
    json += "]";
    g_lastError.clear();
    return dup(json);
}

// 新建数据生成器：目录 + gen.cpp 模板 + desc.txt
AC_API const char* ac_gen_create(int id, const char* name, const char* desc) {
    std::string dir = genProblemRoot(id);
    if (!exists(dir)) {
        std::string err = "题目 " + std::to_string(id) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string gn = name ? name : "";
    if (!validGenName(gn) || gn.find(".cpp") != std::string::npos) {
        std::string err = "生成器名字只能包含字母/数字/下划线/横线（如 juhua）";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string gdir = genDir(id, gn);
    if (exists(gdir)) {
        std::string err = "生成器 " + gn + " 已存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    mkdirs(gdir);
    std::string tpl = kGenTemplate;
    size_t p = tpl.find("{name}");
    if (p != std::string::npos) tpl.replace(p, 6, gn);
    writeFile(genSrcPath(id, gn), tpl);
    writeFile(genDescPath(id, gn), desc ? desc : "");
    g_lastError.clear();
    return dup("{\"ok\":true,\"name\":\"" + jsonEscape(gn) + "\"}");
}

// 读取某个生成器当前代码 + 描述 + 路径（不存在返回 ok:false）
AC_API const char* ac_gen_get_current(int id, const char* name) {
    std::string gn = name ? name : "";
    if (!validGenName(gn) || (!exists(genSrcPath(id, gn)))) {
        std::string err = "生成器 " + gn + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string src = genSrcPath(id, gn);
    std::string code = readFile(src);
    std::string desc = readFile(genDescPath(id, gn));
    std::string json = "{\"ok\":true"
        ",\"name\":\"" + jsonEscape(gn) + "\""
        ",\"desc\":\"" + jsonEscape(desc) + "\""
        ",\"code\":\"" + jsonEscape(code) + "\""
        ",\"genPath\":\"" + jsonEscape(src) + "\""
        ",\"outDir\":\"" + jsonEscape(genOutDir(id, gn)) + "\"}";
    g_lastError.clear();
    return dup(json);
}

// 修改生成器描述
AC_API const char* ac_gen_set_desc(int id, const char* name, const char* desc) {
    std::string gn = name ? name : "";
    if (!validGenName(gn) || !exists(genSrcPath(id, gn))) {
        std::string err = "生成器 " + gn + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    writeFile(genDescPath(id, gn), desc ? desc : "");
    g_lastError.clear();
    return dup("{\"ok\":true}");
}

// 保存生成器代码（直接覆盖 gen.cpp，不再保留历史版本）
AC_API const char* ac_gen_save(int id, const char* name, const char* code) {
    std::string gn = name ? name : "";
    std::string dir = genProblemRoot(id);
    if (!exists(dir)) {
        std::string err = "题目 " + std::to_string(id) + " 不存在";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    if (!validGenName(gn)) {
        std::string err = "生成器名字不合法";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    mkdirs(genDir(id, gn));
    writeFile(genSrcPath(id, gn), code ? code : "");
    g_lastError.clear();
    return dup("{\"ok\":true}");
}

// 编译某个生成器 → temp\{id}\{name}.exe
AC_API const char* ac_gen_compile(int id, const char* name) {
    std::string gn = name ? name : "";
    std::string src = genSrcPath(id, gn);
    if (!exists(src)) {
        std::string err = "请先保存生成器代码";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string exe = genExePath(id, gn);
    mkdirs(g_temp + "\\" + std::to_string(id));
    std::string err;
    bool ok = oj::compile_cpp(src, exe, err);
    g_lastError = ok ? "" : err;
    if (ok) return dup("{\"ok\":true}");
    return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
}

// 运行生成器：每个测试点独立运行一次，stdout 直接重定向到 {题目}/{生成器}/i.in
AC_API const char* ac_gen_run(int id, const char* name, int n) {
    std::string gn = name ? name : "";
    if (n <= 0) n = 1;
    std::string exe = genExePath(id, gn);
    if (!exists(exe)) {
        std::string src = genSrcPath(id, gn);
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
    std::string outDir = genOutDir(id, gn);
    mkdirs(outDir);

    // 清空旧的 .in / .out（避免上次残留），保证判题数据与本次生成一致
    {
        for (const char* ext : { ".in", ".out" }) {
            std::string pat = outDir + "\\*" + ext;
            WIN32_FIND_DATAA fd;
            HANDLE h = FindFirstFileA(pat.c_str(), &fd);
            if (h != INVALID_HANDLE_VALUE) {
                do {
                    if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
                        remove((outDir + "\\" + fd.cFileName).c_str());
                } while (FindNextFileA(h, &fd));
                FindClose(h);
            }
        }
    }

    // 生成器协议（cout 模式）：每个测试点运行一次，stdout 重定向到 i.in。
    // argv[1]=输出目录, argv[2]=随机种子, argv[3]=总组数 n, argv[4]=当前组号 i（1 起）。
    DWORD baseSeed = GetTickCount();
    int generated = 0;
    std::vector<std::string> fails;
    for (int i = 1; i <= n; ++i) {
        DWORD seed = baseSeed + i;
        std::string args = "\"" + outDir + "\" " + std::to_string(seed)
                         + " " + std::to_string(n) + " " + std::to_string(i);
        std::string outFile = outDir + "\\" + std::to_string(i) + ".in";
        RunRedirect rr = runRedirect(exe, args, outDir, outFile, 60000);
        if (rr.timeout) { fails.push_back(std::to_string(i) + ".in：超时"); continue; }
        if (!rr.ok) {
            std::string e = std::to_string(i) + ".in：退出码 " + std::to_string(rr.exitCode);
            if (!rr.errText.empty()) e += "：" + rr.errText.substr(0, 120);
            fails.push_back(e);
            continue;
        }
        ++generated;
    }

    if (generated == 0) {
        std::string err = "生成器没有产出任何数据";
        if (!fails.empty()) err += "（" + fails.front() + "）";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }

    std::string json = "{\"ok\":true,\"generated\":" + std::to_string(generated)
        + ",\"total\":" + std::to_string(n)
        + ",\"outDir\":\"" + jsonEscape(outDir) + "\""
        + ",\"fails\":[";
    for (size_t i = 0; i < fails.size(); ++i) {
        if (i) json += ",";
        json += "\"" + jsonEscape(fails[i]) + "\"";
    }
    json += "]}";
    g_lastError.clear();
    return dup(json);
}

// 生成器目录下的数据文件列表
AC_API const char* ac_gen_list_files(int id, const char* name) {
    std::string gn = name ? name : "";
    std::string dir = genDir(id, gn);
    std::vector<std::pair<int, std::string>> items;
    std::string pattern = dir + "\\*.in";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            std::string fname = fd.cFileName;
            std::string base = fname.substr(0, fname.size() - 3);
            int num = atoi(base.c_str());
            items.push_back({num, fname});
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
    g_lastError.clear();
    return dup(json);
}

// 读取某个生成器目录下数据文件内容（限 200KB）
AC_API const char* ac_gen_get_file(int id, const char* name, const char* file) {
    if (!file || !*file) {
        std::string err = "文件名为空";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string gn = name ? name : "";
    std::string path = genDir(id, gn) + "\\" + file;
    if (!exists(path)) {
        std::string err = "文件不存在: " + std::string(file);
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

// 跨题查找生成器代码（大小写不敏感，最多 30 条）
AC_API const char* ac_gen_search(const char* keyword) {
    if (!keyword || !*keyword) {
        std::string err = "关键字为空";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string kw = toLower(keyword);
    std::string json = "[";
    bool first = true;
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
        int pid = atoi(d.c_str());
        std::string st = readFile(g_root + "\\" + d + "\\statement.txt");
        size_t nl = st.find('\n');
        std::string title = (nl == std::string::npos) ? st : st.substr(0, nl);
        for (auto& gn : listGenerators(pid)) {
            std::string content = readFile(genSrcPath(pid, gn));
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
                    json += "{\"pid\":" + std::to_string(pid)
                         + ",\"gen\":\"" + jsonEscape(gn) + "\""
                         + ",\"title\":\"" + jsonEscape(title) + "\""
                         + ",\"lineNo\":" + std::to_string(lineNo)
                         + ",\"line\":\"" + jsonEscape(trimmed) + "\""
                         + ",\"keyword\":\"" + jsonEscape(keyword) + "\"}";
                    ++n;
                }
            }
        }
    }
    json += "]";
    g_lastError.clear();
    return dup(json);
}

// 把某生成器某个 .in 复制到题目测试数据目录根（兼容旧流程；新流程生成器目录即数据源）
AC_API const char* ac_gen_import_to_problem(int id, const char* name, const char* filename) {
    if (!filename || !*filename) {
        std::string err = "文件名为空";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string gn = name ? name : "";
    std::string src = genDir(id, gn) + "\\" + filename;
    if (!exists(src)) {
        std::string err = "文件不存在: " + std::string(filename);
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    std::string dst = genProblemRoot(id) + "\\" + filename;
    if (!CopyFileA(src.c_str(), dst.c_str(), FALSE)) {
        std::string err = "复制失败 (err=" + std::to_string(GetLastError()) + ")";
        g_lastError = err;
        return dup("{\"ok\":false,\"error\":\"" + jsonEscape(err) + "\"}");
    }
    g_lastError.clear();
    return dup("{\"ok\":true}");
}

} // extern "C"