namespace OjCodex;

/// <summary>
/// 一次指令运行的账（用量 + 结果）。RunUid 由客户端生成，用来和下面的改动记录关联 ——
/// 先记运行还是先改题面都不影响关联，避免依赖服务端自增 id 的时序。
/// </summary>
public sealed record CodexRunRecord(
    string RunUid,
    string Username,
    string Model,
    string Effort,
    string Instruction,
    int ToolCalls,
    long ElapsedMs,
    bool Ok,
    string Error,
    CodexUsage Usage,
    long ContextTokens,
    double CostUsd);

/// <summary>
/// 一次题面写回的改动记录（前后对照）。
/// 这是审计的核心：Codex 能直接改写题库，事后必须能回答「谁、按哪条指令、把什么改成了什么」。
/// </summary>
public sealed record CodexStatementAudit(
    string RunUid,
    int ProblemId,
    string Username,
    string BeforeTitle,
    string BeforeDescription,
    string AfterTitle,
    string AfterDescription,
    string Tags,
    int TimeMs,
    int MemMb,
    string Message);

/// <summary>
/// 审计与用量的落库出口。OjCodex 只依赖这个接口，不关心后端是中间件、本地库还是别的。
///
/// 实现方必须**自行吞掉异常**：记录失败绝不能影响用户正在做的改题操作。
/// </summary>
public interface ICodexRecorder
{
    Task RecordRunAsync(CodexRunRecord record, CancellationToken ct = default);
    Task RecordStatementAsync(CodexStatementAudit audit, CancellationToken ct = default);
}
