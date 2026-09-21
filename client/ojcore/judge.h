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

// 对 testDir 下各数据生成器子目录（含 gen.cpp 的二级目录）的 *.in 运行 exe，
// 返回逐测试点结果。生成器子目录名体现在用例名（如 "juhua/1"），未通过的用例
// info 为该生成器 desc.txt 的描述文本。按文本比对（normalize 后逐行）。
std::vector<CaseResult> run_tests(const std::string& exeFile,
                                  const std::string& testDir,
                                  const std::string& tempOut,
                                  const JudgeOptions& opt);

// 汇总整体判定：CE > TLE > RE/SE > WA > AC
std::string summarize(const std::vector<CaseResult>& cases);

} // namespace oj
