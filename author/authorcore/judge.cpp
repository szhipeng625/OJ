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
RunOutcome run_one(const std::string& exe, const std::string& inFile,
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
    QueryPerformanceCounter(&t0);

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

// 运行 spj：spjExe userOut ansFile inFile，退出码 0 = 通过
static RunOutcome run_spj_one(const std::string& spjExe, const std::string& userOut,
                              const std::string& ansFile, const std::string& inFile,
                              DWORD timeoutMs) {
    RunOutcome r = {0, 0, 0};
    STARTUPINFOA si; ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi; ZeroMemory(&pi, sizeof(pi));

    char cmd[4096];
    snprintf(cmd, sizeof(cmd), "\"%s\" \"%s\" \"%s\" \"%s\"",
             spjExe.c_str(), userOut.c_str(), ansFile.c_str(), inFile.c_str());

    LARGE_INTEGER t0, t1, freq;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t0);

    if (!CreateProcessA(NULL, cmd, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
        r.status = 3; return r;
    }
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
    return r;
}

std::vector<CaseResult> run_tests(const std::string& exeFile, const std::string& testDir,
                                  const std::string& tempOut, const JudgeOptions& opt) {
    std::vector<CaseResult> out;
    auto ins = listInFiles(testDir);
    int idx = 0;
    for (auto& f : ins) {
        idx++;
        std::string base = f.substr(0, f.size() - 3);
        std::string inFile  = testDir + "\\" + f;
        std::string ansFile = testDir + "\\" + base + ".out";
        RunOutcome r = run_one(exeFile, inFile, tempOut, opt.timeoutMs, opt.memBytes);

        CaseResult c;
        c.name = "#" + std::to_string(idx);
        c.timeMs = r.ms;
        if (r.status == 3)      { c.verdict = "SE"; c.passed = false; }
        else if (r.status == 1) { c.verdict = "TLE"; c.passed = false; }
        else if (r.status == 2) { c.verdict = "RE"; c.passed = false; }
        else if (!opt.spjExe.empty()) {
            // Special Judge：spj user.out ans.out in，退出码 0 = AC
            RunOutcome sp = run_spj_one(opt.spjExe, tempOut, ansFile, inFile, 5000);
            if (sp.status == 3)      { c.verdict = "SE"; c.passed = false; c.info = "spj 启动失败"; }
            else if (sp.status == 1) { c.verdict = "WA"; c.passed = false; c.info = "spj 超时"; }
            else if (sp.exitCode != 0) { c.verdict = "WA"; c.passed = false; c.info = "special judge 未通过"; }
            else { c.verdict = "AC"; c.passed = true; }
        } else {
            std::string u = normalize(readAll(tempOut));
            std::string a = normalize(readAll(ansFile));
            if (u == a) { c.verdict = "AC"; c.passed = true; }
            else        { c.verdict = "WA"; c.passed = false; c.info = "输出与标准答案不一致"; }
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

} // namespace oj
