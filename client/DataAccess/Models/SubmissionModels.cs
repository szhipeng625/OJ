namespace client.DataAccess.Models;

/// <summary>单个测试点判题结果</summary>
public record CaseResult(string Name, int TimeMs, bool Passed, string Info);

/// <summary>一次提交判题的整体结果</summary>
public record SubmitResult(long Id, string Verdict, string Detail, List<CaseResult> Cases);
