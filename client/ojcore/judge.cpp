// judge.cpp
#include "judge.h"

#include <windows.h>
#include <shlwapi.h>

#include <algorithm>
#include <fstream>
#include <sstream>

#include <cstdio>
#pragma comment(lib, "shlwapi.lib")

namespace oj {

static std::vector<std::string> listInFiles(const std::string& dir) {
    std::vector<std::string> res;
    std::string pattern = dir + "\\*.in";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return res;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        // 跳过样例文件（sample.in 仅供展示，不参与判题）
        if (strncmp(fd.cFileName, "sample", 6) == 0) continue;
        res.push_back(fd.cFileName);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    std::sort(res.begin(), res.end());
    return res;
}

static std::string readAll(const std::string& path) {
    std::ifstream f(path, std::ios::binary);
    if (!f) return "";
    std::stringstream ss; ss << f.rdbuf();
    return ss.str();
}

// 去掉 UTF-8 BOM（EF BB BF），避免影响首行解析/比对
static std::string stripBom(const std::string& raw) {
    if (raw.size() >= 3 &&
        (unsigned char)raw[0] == 0xEF &&
        (unsigned char)raw[1] == 0xBB &&
        (unsigned char)raw[2] == 0xBF)
        return raw.substr(3);
    return raw;
}

static std::string normalize(const std::string& raw) {
    std::string noCR;
    for (char c : stripBom(raw)) if (c != '\r') noCR += c;
    std::istringstream iss(noCR);
    std::string line;
    std::vector<std::string> lines;
    while (std::getline(iss, line)) {
        while (!line.empty() && (line.back() == ' ' || line.back() == '\t'))
            line.pop_back();
        lines.push_back(line);
    }
    while (!lines.empty() && lines.back().empty()) lines.pop_back();
    std::string out;
    for (auto& l : lines) out += l + "\n";
    return out;
}

// 运行一个 exe，重定向 stdin/stdout，限时/限内存。
// status: 0=正常 1=TLE 2=崩溃/非零退出 3=启动失败
struct RunOutcome { int status; DWORD exitCode; long long ms; };

static RunOutcome run_one(const std::string& exe, const std::string& inFile,
                          const std::string& userOut, DWORD timeoutMs, SIZE_T memBytes) {
    RunOutcome r = {0, 0, 0};
    SECURITY_ATTRIBUTES sa;
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;
    sa.lpSecurityDescriptor = NULL;

    // 输入文件若带 UTF-8 BOM，生成去 BOM 的临时副本作为 stdin（避免 cin 解析失败）
    std::string stdinFile = inFile;
    std::string bomCopy;
    std::string inRaw = readAll(inFile);
    if (inRaw.size() >= 3 &&
        (unsigned char)inRaw[0] == 0xEF && (unsigned char)inRaw[1] == 0xBB && (unsigned char)inRaw[2] == 0xBF) {
        bomCopy = inFile + ".nobom";
        std::ofstream o(bomCopy, std::ios::binary | std::ios::trunc);
        o << inRaw.substr(3);
        stdinFile = bomCopy;
    }

    HANDLE hIn = CreateFileA(stdinFile.c_str(), GENERIC_READ, FILE_SHARE_READ,
                             &sa, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    HANDLE hOut = CreateFileA(userOut.c_str(), GENERIC_WRITE,
                             FILE_SHARE_READ | FILE_SHARE_WRITE,
                             &sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hIn == INVALID_HANDLE_VALUE || hOut == INVALID_HANDLE_VALUE) {
        if (hIn != INVALID_HANDLE_VALUE) CloseHandle(hIn);
        if (hOut != INVALID_HANDLE_VALUE) CloseHandle(hOut);
        if (!bomCopy.empty()) DeleteFileA(bomCopy.c_str());
        r.status = 3; return r;
    }

    STARTUPINFOA si; ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput  = hIn;
    si.hStdOutput = hOut;
    si.hStdError  = hOut;
    PROCESS_INFORMATION pi; ZeroMemory(&pi, sizeof(pi));

    char cmd[MAX_PATH * 2];
    snprintf(cmd, sizeof(cmd), "\"%s\"", exe.c_str());

    LARGE_INTEGER t0, t1, freq;
    QueryPerformanceFrequency(&freq);

    BOOL ok = CreateProcessA(NULL, cmd, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi);
    if (!ok) {
        CloseHandle(hIn); CloseHandle(hOut);
        if (!bomCopy.empty()) DeleteFileA(bomCopy.c_str());
        r.status = 3; return r;
    }

    HANDLE hJob = NULL;
    if (memBytes > 0) {
        hJob = CreateJobObjectA(NULL, NULL);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION jeli = {};
        jeli.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        jeli.ProcessMemoryLimit = memBytes;
        SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, &jeli, sizeof(jeli));
        AssignProcessToJobObject(hJob, pi.hProcess);
    }

    // 进程启动完成后开始计时：不计入进程创建开销，报告的是实际运行时间
    QueryPerformanceCounter(&t0);
    DWORD wr = WaitForSingleObject(pi.hProcess, timeoutMs);
    QueryPerformanceCounter(&t1);
    r.ms = (long long)((t1.QuadPart - t0.QuadPart) * 1000 / freq.QuadPart);

    if (wr == WAIT_TIMEOUT) {
        TerminateProcess(pi.hProcess, 1);
        r.status = 1;
    } else {
        GetExitCodeProcess(pi.hProcess, &r.exitCode);
        if (r.exitCode != 0) r.status = 2;
    }

    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    CloseHandle(hIn); CloseHandle(hOut);
    if (hJob) CloseHandle(hJob);
    if (!bomCopy.empty()) DeleteFileA(bomCopy.c_str());
    return r;
}

bool compile_cpp(const std::string& srcFile, const std::string& exeFile, std::string& errMsg) {
    SECURITY_ATTRIBUTES sa; sa.nLength = sizeof(sa); sa.bInheritHandle = TRUE; sa.lpSecurityDescriptor = NULL;
    std::string errFile = exeFile + ".err";
    HANDLE hErr = CreateFileA(errFile.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              &sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);

    char cmd[1024];
    snprintf(cmd, sizeof(cmd), "g++ -O2 -o \"%s\" \"%s\"",
             exeFile.c_str(), srcFile.c_str());

    STARTUPINFOA si; ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si); si.dwFlags = STARTF_USESTDHANDLES; si.hStdError = hErr; si.hStdOutput = hErr;
    PROCESS_INFORMATION pi; ZeroMemory(&pi, sizeof(pi));

    BOOL ok = CreateProcessA(NULL, cmd, NULL, NULL, TRUE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi);
    if (!ok) { if (hErr) CloseHandle(hErr); errMsg = "无法启动 g++"; return false; }
    WaitForSingleObject(pi.hProcess, 60000);
    DWORD exitCode = 1; GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    if (hErr) CloseHandle(hErr);

    errMsg = readAll(errFile);
    DeleteFileA(errFile.c_str());
    if (exitCode != 0) return false;
    return GetFileAttributesA(exeFile.c_str()) != INVALID_FILE_ATTRIBUTES;
}

// 去掉字符串首尾空白（空格 / 制表 / 回车换行）
static std::string trim(const std::string& s) {
    size_t a = s.find_first_not_of(" \t\r\n");
    if (a == std::string::npos) return "";
    size_t b = s.find_last_not_of(" \t\r\n");
    return s.substr(a, b - a + 1);
}

// 收集 testDir 下各数据生成器子目录（含 gen.cpp 的二级目录）的所有 *.in。
// 返回 {相对路径（如 "juhua/1.in"）, 生成器目录名}；不再读取题目根目录下的 .in。
static std::vector<std::pair<std::string, std::string>> collectAllIn(const std::string& testDir) {
    std::vector<std::pair<std::string, std::string>> res;

    std::string pat = testDir + "\\*";
    WIN32_FIND_DATAA fd; HANDLE h = FindFirstFileA(pat.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return res;
    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        std::string n = fd.cFileName;
        if (n == "." || n == "..") continue;
        if (n == "history" || n == "gen_history") continue;
        std::string sub = testDir + "\\" + n;
        // 只有数据生成器目录（含 gen.cpp）才作为判题数据源
        if (GetFileAttributesA((sub + "\\gen.cpp").c_str()) == INVALID_FILE_ATTRIBUTES) continue;
        auto subIns = listInFiles(sub);
        for (auto& f : subIns) res.push_back({n + "/" + f, n});
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    std::sort(res.begin(), res.end());
    return res;
}
std::vector<CaseResult> run_tests(const std::string& exeFile, const std::string& testDir,
                                  const std::string& tempOut, const JudgeOptions& opt) {
    std::vector<CaseResult> out;
    auto ins = collectAllIn(testDir);
    int idx = 0;
    for (auto& item : ins) {
        idx++;
        const std::string& inp = item.first;
        const std::string& genName = item.second;
        // 该生成器目录下的描述文本（desc.txt），未通过时返回给用户
        std::string genDesc = trim(readAll(testDir + "\\" + genName + "\\desc.txt"));

        std::string slash = inp;
        for (auto& ch : slash) if (ch == '/') ch = '\\';
        std::string base = slash.substr(0, slash.size() - 3);
        std::string inFile  = testDir + "\\" + slash;
        std::string ansFile = testDir + "\\" + base + ".out";
        RunOutcome r = run_one(exeFile, inFile, tempOut, opt.timeoutMs, opt.memBytes);

        CaseResult c;
        c.name = inp.substr(0, inp.size() - 3);   // 如 "juhua/1"
        c.timeMs = r.ms;
        if (r.status == 3)      { c.verdict = "SE";  c.passed = false; c.info = genDesc; }
        else if (r.status == 1) { c.verdict = "TLE"; c.passed = false; c.info = genDesc; }
        else if (r.status == 2) { c.verdict = "RE";  c.passed = false; c.info = genDesc; }
        else {
            std::string u = normalize(readAll(tempOut));
            std::string a = normalize(readAll(ansFile));
            if (u == a) { c.verdict = "AC"; c.passed = true; }
            else        { c.verdict = "WA"; c.passed = false;
                          c.info = genDesc.empty() ? "输出与标准答案不一致" : genDesc; }
        }
        out.push_back(c);
    }
    DeleteFileA(tempOut.c_str());
    return out;
}

std::string summarize(const std::vector<CaseResult>& cases) {
    if (cases.empty()) return "WA";
    for (auto& c : cases) if (c.verdict == "TLE") return "TLE";
    for (auto& c : cases) if (c.verdict == "RE")  return "RE";
    for (auto& c : cases) if (c.verdict == "SE")  return "SE";
    for (auto& c : cases) if (c.verdict == "WA")  return "WA";
    return "AC";
}

DebugResult debug_run(const std::string& workDir, const std::string& code,
                      const std::string& input, DWORD timeoutMs) {
    DebugResult r;
    std::string src = workDir + "\\debug.cpp";
    std::string exe = workDir + "\\debug.exe";
    std::string inFile  = workDir + "\\debug.in";
    std::string outFile = workDir + "\\debug.out";
    std::string errFile = workDir + "\\debug.err";

    {
        std::ofstream f(src, std::ios::binary | std::ios::trunc);
        if (!f) { r.compileError = "无法写入源文件"; return r; }
        f << code;
    }
    {
        std::ofstream f(inFile, std::ios::binary | std::ios::trunc);
        f << input;
    }

    std::string cerrMsg;
    if (!compile_cpp(src, exe, cerrMsg)) {
        r.compileError = cerrMsg;
        DeleteFileA(src.c_str());
        DeleteFileA(inFile.c_str());
        return r;
    }

    SECURITY_ATTRIBUTES sa;
    sa.nLength = sizeof(sa); sa.bInheritHandle = TRUE; sa.lpSecurityDescriptor = NULL;
    HANDLE hIn  = CreateFileA(inFile.c_str(), GENERIC_READ, FILE_SHARE_READ,
                              &sa, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    HANDLE hOut = CreateFileA(outFile.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              &sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    HANDLE hErr = CreateFileA(errFile.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              &sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hIn == INVALID_HANDLE_VALUE || hOut == INVALID_HANDLE_VALUE || hErr == INVALID_HANDLE_VALUE) {
        if (hIn  != INVALID_HANDLE_VALUE) CloseHandle(hIn);
        if (hOut != INVALID_HANDLE_VALUE) CloseHandle(hOut);
        if (hErr != INVALID_HANDLE_VALUE) CloseHandle(hErr);
        DeleteFileA(src.c_str()); DeleteFileA(exe.c_str()); DeleteFileA(inFile.c_str());
        r.runError = "无法创建输入/输出文件";
        return r;
    }

    STARTUPINFOA si; ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = hIn; si.hStdOutput = hOut; si.hStdError = hErr;
    PROCESS_INFORMATION pi; ZeroMemory(&pi, sizeof(pi));

    std::string cmd = "\"" + exe + "\"";
    std::vector<char> cmdBuf(cmd.begin(), cmd.end()); cmdBuf.push_back('\0');
    BOOL started = CreateProcessA(NULL, cmdBuf.data(), NULL, NULL, TRUE, 0, NULL,
                                  workDir.empty() ? NULL : workDir.c_str(), &si, &pi);
    CloseHandle(hIn); CloseHandle(hOut); CloseHandle(hErr);
    if (!started) {
        DeleteFileA(src.c_str()); DeleteFileA(exe.c_str()); DeleteFileA(inFile.c_str());
        r.runError = "启动程序失败";
        return r;
    }

    DWORD wr = WaitForSingleObject(pi.hProcess, timeoutMs);
    if (wr == WAIT_TIMEOUT) { TerminateProcess(pi.hProcess, 1); r.timeout = true; }
    GetExitCodeProcess(pi.hProcess, &r.exitCode);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);

    r.output = readAll(outFile);
    r.runError = readAll(errFile);
    r.ok = !r.timeout && r.exitCode == 0 && r.compileError.empty();

    DeleteFileA(src.c_str()); DeleteFileA(exe.c_str()); DeleteFileA(inFile.c_str());
    DeleteFileA(outFile.c_str()); DeleteFileA(errFile.c_str());
    return r;
}

DebugTestResult debug_test(const std::string& workDir, const std::string& code,
                           const std::string& inFile, const std::string& outFile,
                           DWORD timeoutMs) {
    DebugTestResult r;
    std::string src = workDir + "\\debug.cpp";
    std::string exe = workDir + "\\debug.exe";
    std::string outTmp = workDir + "\\debug.out";
    std::string errTmp = workDir + "\\debug.err";

    {
        std::ofstream f(src, std::ios::binary | std::ios::trunc);
        if (!f) { r.compileError = "无法写入源文件"; return r; }
        f << code;
    }

    std::string cerrMsg;
    if (!compile_cpp(src, exe, cerrMsg)) {
        r.compileError = cerrMsg;
        DeleteFileA(src.c_str());
        return r;
    }

    SECURITY_ATTRIBUTES sa;
    sa.nLength = sizeof(sa); sa.bInheritHandle = TRUE; sa.lpSecurityDescriptor = NULL;
    HANDLE hIn = CreateFileA(inFile.c_str(), GENERIC_READ, FILE_SHARE_READ,
                             &sa, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hIn == INVALID_HANDLE_VALUE) {
        DeleteFileA(src.c_str()); DeleteFileA(exe.c_str());
        r.runError = "未导入样例输入(sample.in)";
        return r;
    }
    HANDLE hOut = CreateFileA(outTmp.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              &sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    HANDLE hErr = CreateFileA(errTmp.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                              &sa, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hOut == INVALID_HANDLE_VALUE || hErr == INVALID_HANDLE_VALUE) {
        CloseHandle(hIn);
        if (hOut != INVALID_HANDLE_VALUE) CloseHandle(hOut);
        if (hErr != INVALID_HANDLE_VALUE) CloseHandle(hErr);
        DeleteFileA(src.c_str()); DeleteFileA(exe.c_str());
        r.runError = "无法创建输出文件";
        return r;
    }

    STARTUPINFOA si; ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = hIn; si.hStdOutput = hOut; si.hStdError = hErr;
    PROCESS_INFORMATION pi; ZeroMemory(&pi, sizeof(pi));

    std::string cmd = "\"" + exe + "\"";
    std::vector<char> cmdBuf(cmd.begin(), cmd.end()); cmdBuf.push_back('\0');
    BOOL started = CreateProcessA(NULL, cmdBuf.data(), NULL, NULL, TRUE, 0, NULL,
                                  workDir.empty() ? NULL : workDir.c_str(), &si, &pi);
    CloseHandle(hIn); CloseHandle(hOut); CloseHandle(hErr);
    if (!started) {
        DeleteFileA(src.c_str()); DeleteFileA(exe.c_str());
        r.runError = "启动程序失败";
        return r;
    }

    DWORD wr = WaitForSingleObject(pi.hProcess, timeoutMs);
    if (wr == WAIT_TIMEOUT) { TerminateProcess(pi.hProcess, 1); r.timeout = true; }
    GetExitCodeProcess(pi.hProcess, &r.exitCode);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);

    r.output = readAll(outTmp);
    r.runError = readAll(errTmp);
    r.expected = readAll(outFile);
    r.passed = (normalize(r.output) == normalize(r.expected));
    r.ok = !r.timeout && r.exitCode == 0 && r.compileError.empty();

    DeleteFileA(src.c_str()); DeleteFileA(exe.c_str());
    DeleteFileA(outTmp.c_str()); DeleteFileA(errTmp.c_str());
    return r;
}

} // namespace oj
