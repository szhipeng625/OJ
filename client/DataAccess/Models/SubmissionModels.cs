namespace client.DataAccess.Models;

/// <summary>单个测试点判题结果</summary>
public record CaseResult(string Name, int TimeMs, bool Passed, string Info);

/// <summary>一次提交判题的整体结果</summary>
public record SubmitResult(long Id, string Verdict, string Detail, List<CaseResult> Cases);

/// <summary>本地调试（编译 + 运行）的结果</summary>
public record DebugRunResult(bool Ok, string CompileError, string Output, string RunError, bool Timeout, int ExitCode);

/// <summary>本地调试（样例比对）的结果</summary>
public record DebugTestResult(bool Ok, string CompileError, string Output, string Expected,
                              bool Passed, string RunError, bool Timeout, int ExitCode);
