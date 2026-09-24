// judge.h — 判题核心：编译、限时运行、输出比对
#pragma once
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

// 运行一个 exe，重定向 stdin/stdout，限时/限内存。
// status: 0=正常 1=TLE 2=崩溃/非零退出 3=启动失败
struct RunOutcome { int status; DWORD exitCode; long long ms; };

RunOutcome run_one(const std::string& exe, const std::string& inFile,
                   const std::string& userOut, DWORD timeoutMs, SIZE_T memBytes);

// 崩溃类退出码（NTSTATUS 异常码）→ 中文描述；非崩溃返回 nullptr
const char* crash_desc(DWORD code);

// 对 testDir 下每个 *.in 运行 exe，返回逐测试点结果（文本比对）。
std::vector<CaseResult> run_tests(const std::string& exeFile,
                                  const std::string& testDir,
                                  const std::string& tempOut,
                                  const JudgeOptions& opt);

// 汇总整体判定：CE > TLE > RE/SE > WA > AC
std::string summarize(const std::vector<CaseResult>& cases);

} // namespace oj
