// judge.h — 判题核心：编译、限时运行、输出比对
#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <string>
#include <vector>

namespace oj {

struct CaseResult {
    std::string name;     // 如 "#1"
    long long   timeMs;   // 耗时
    bool        passed;   // 是否 AC
    std::string verdict;  // AC/WA/TLE/RE/SE
    std::string info;
};

// 把用户源码编译为 exe。返回 true 成功，false 失败（errMsg 填编译器 stderr）。
bool compile_cpp(const std::string& srcFile, const std::string& exeFile, std::string& errMsg);

// 判题选项
struct JudgeOptions {
    DWORD timeoutMs = 1000;                              // 单测时间限制
    SIZE_T memBytes = 256ull * 1024 * 1024;              // 内存限制（0 不限）
};

// 对 testDir 下各数据生成器子目录（含 gen.cpp 的二级目录）的 *.in 运行 exe，
// 返回逐测试点结果。生成器子目录名体现在用例名（如 "juhua/1"），未通过的用例
// info 为该生成器 desc.txt 的描述文本。按文本比对（normalize 后逐行）。
std::vector<CaseResult> run_tests(const std::string& exeFile,
                                  const std::string& testDir,
                                  const std::string& tempOut,
                                  const JudgeOptions& opt);

// 汇总整体判定：CE > TLE > RE/SE > WA > AC
std::string summarize(const std::vector<CaseResult>& cases);

// 本地调试：编译用户源码并用给定 stdin 运行，返回 stdout/stderr。
// workDir 为临时目录（用于中间文件）。timeoutMs 为运行时限。
struct DebugResult {
    bool ok = false;            // 编译成功且运行退出码为 0
    std::string compileError;   // g++ 编译错误（stderr）
    std::string output;         // 程序 stdout
    std::string runError;       // 程序 stderr
    bool timeout = false;
    DWORD exitCode = 0;
};
DebugResult debug_run(const std::string& workDir, const std::string& code,
                      const std::string& input, DWORD timeoutMs);

// 本地调试（样例比对）：编译并用 inFile 作 stdin 运行，与 outFile 逐行比对。
struct DebugTestResult {
    bool ok = false;            // 编译成功且运行正常
    std::string compileError;
    std::string output;         // 程序 stdout（实际输出）
    std::string expected;       // 期望输出（sample.out）
    bool passed = false;        // 输出比对通过
    std::string runError;       // 程序 stderr
    bool timeout = false;
    DWORD exitCode = 0;
};
DebugTestResult debug_test(const std::string& workDir, const std::string& code,
                           const std::string& inFile, const std::string& outFile,
                           DWORD timeoutMs);

} // namespace oj
