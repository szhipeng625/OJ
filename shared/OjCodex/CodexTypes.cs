namespace OjCodex;

/// <summary>一次 API 调用的 token 用量（对应 response.Usage）。</summary>
public readonly record struct CodexUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens)
{
    public static readonly CodexUsage Zero = new(0, 0, 0, 0);

    public CodexUsage Plus(CodexUsage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheReadTokens + other.CacheReadTokens,
        CacheWriteTokens + other.CacheWriteTokens);

    /// <summary>计费口径的输入总 token（含缓存读写）。</summary>
    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>
    /// 缓存命中率 = 缓存读 / 计费输入总量。
    /// 这是判断「降本是否生效」的核心指标：越高说明前缀越稳定、越省 token。
    /// </summary>
    public double CacheHitRate => TotalInputTokens <= 0 ? 0 : (double)CacheReadTokens / TotalInputTokens;
}

/// <summary>看板上的一张卡片：一次用户指令 = 一个任务。</summary>
public sealed class CodexTask
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public string Instruction { get; set; } = "";
    public CodexTaskState State { get; set; } = CodexTaskState.Pending;
    public DateTime StartedAt { get; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    public string Summary { get; set; } = "";
    public string Error { get; set; } = "";
    public CodexUsage Usage { get; set; } = CodexUsage.Zero;
    public List<CodexStep> Steps { get; } = new();

    public TimeSpan Elapsed => (FinishedAt ?? DateTime.Now) - StartedAt;
}

public enum CodexTaskState { Pending, Running, Done, Failed }

/// <summary>看板上卡片里的一步：一次思考 / 一次工具调用 / 一次回复。</summary>
public sealed class CodexStep
{
    public CodexStepKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string ToolName { get; init; } = "";
    public bool IsError { get; init; }
    public DateTime At { get; } = DateTime.Now;
}

public enum CodexStepKind { Thinking, Text, ToolCall, ToolResult, Info }

/// <summary>工具调用记录（供面板与 MCP 日志展示）。</summary>
public sealed record CodexToolCall(string Name, string InputJson, string Result, bool IsError);

/// <summary>一次用户指令跑完后的结果。</summary>
public sealed record CodexRunResult(
    bool Ok,
    string Answer,
    CodexUsage Usage,
    int ToolCallCount,
    string Error = "",
    long ContextTokens = 0,
    int SessionMessages = 0);
