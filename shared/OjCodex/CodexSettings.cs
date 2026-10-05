namespace OjCodex;

/// <summary>
/// ACMDOGCodex 运行参数。由出题端从 config.xml 的 &lt;codex&gt; 节读出后传入，
/// 也可在 Codex 面板里临时改（改完写回 XML）。
/// </summary>
public sealed class CodexSettings
{
    /// <summary>Anthropic API Key；为空时回退环境变量 ANTHROPIC_API_KEY。</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>自定义网关地址（留空走官方）。对应 ANTHROPIC_BASE_URL。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>模型 ID。默认 Claude Opus 5.5。</summary>
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>思考力度：low / medium / high / xhigh / max。默认 high（改题面属推理型任务）。</summary>
    public string Effort { get; set; } = "high";

    /// <summary>单次回复的最大输出 token。</summary>
    public int MaxTokens { get; set; } = 16000;

    /// <summary>单次指令内最多跑几轮工具调用，防止失控烧 token。</summary>
    public int MaxToolRounds { get; set; } = 12;

    /// <summary>单次指令最多花多少输出 token（软上限，超了就停）。0 = 不限。</summary>
    public int RunOutputTokenBudget { get; set; } = 60000;

    /// <summary>是否开启 prompt caching（缓存命中是主要降本手段）。</summary>
    public bool EnablePromptCaching { get; set; } = true;

    /// <summary>
    /// 打开面板 / 切题 / 改思考链后，后台先发一次 max_tokens=0 的预热请求，
    /// 把 tools+system 前缀写进缓存。用户真正点「发送」时首包就是缓存读，省一次写入等待。
    /// 总写入次数不变（本来第一次也要写），但把等待挪到了用户打字的时候。
    /// </summary>
    public bool EnableCacheWarmup { get; set; } = true;

    /// <summary>
    /// 同一轮里模型发起的多个工具调用并发执行（各自仍走按题串行闸门）。
    /// 工具之间有依赖时并发不会出错 —— 依赖表现为「分轮调用」，不会挤在同一轮。
    /// </summary>
    public bool UseParallelTools { get; set; } = true;

    /// <summary>
    /// 会话里最多保留多少条消息，超了就按「整对」裁掉最早的往返。
    /// 注意：裁掉的起点之后，消息尾部的缓存会失效一次；system 前缀不受影响。
    /// </summary>
    public int MaxContextMessages { get; set; } = 40;

    /// <summary>单条工具结果最多回灌多少字符（超长会截断，避免把上下文撑爆）。</summary>
    public int MaxToolResultChars { get; set; } = 20000;

    /// <summary>
    /// 用向量检索挑出与本次指令最相关的题目，把它们的摘要放进首条用户消息，
    /// 省掉模型「先 oj_list_problems 再 oj_get_problem」的兜圈。
    /// 题目数不超过 RetrievalTopK 时会自动跳过（检索没有意义）。
    /// </summary>
    public bool EnableRetrieval { get; set; } = true;

    /// <summary>检索返回的相关题目条数。</summary>
    public int RetrievalTopK { get; set; } = 8;

    /// <summary>检索结果里每条题目摘要最多多少字符 —— 这就是「检索结果转成 token」的预算闸门。</summary>
    public int RetrievalSnippetChars { get; set; } = 240;

    /// <summary>缓存 TTL：5m / 1h。1h 更省 token 但写入更贵。</summary>
    public string CacheTtl { get; set; } = "5m";

    /// <summary>是否显示思考摘要（display=summarized）；关掉可省一点带宽。</summary>
    public bool ShowThinking { get; set; } = true;

    /// <summary>是否启用 MCP 服务（Author.exe --mcp）。</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>自定义思考逻辑链（空则用 <see cref="ThinkingChain.Default"/>）。</summary>
    public string ThinkingChain { get; set; } = "";

    public string EffectiveApiKey =>
        string.IsNullOrWhiteSpace(ApiKey) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "" : ApiKey;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(EffectiveApiKey);
}

/// <summary>
/// 价格表（美元 / 百万 token），用于面板上的成本估算。
/// 数值取自 Claude Opus 5.5：输入 $4、输出 $20、缓存读 $0.20；缓存写按 1.25×输入计。
/// 换模型时改这里即可。
/// </summary>
public sealed class CodexPricing
{
    public double InputPerMTok { get; init; } = 4.00;
    public double OutputPerMTok { get; init; } = 20.00;
    public double CacheReadPerMTok { get; init; } = 0.20;
    public double CacheWritePerMTok { get; init; } = 5.00;

    public static readonly CodexPricing Default = new();

    /// <summary>按四种用量估算总成本（美元）。</summary>
    public double Estimate(CodexUsage u) =>
        u.InputTokens / 1_000_000.0 * InputPerMTok
        + u.OutputTokens / 1_000_000.0 * OutputPerMTok
        + u.CacheReadTokens / 1_000_000.0 * CacheReadPerMTok
        + u.CacheWriteTokens / 1_000_000.0 * CacheWritePerMTok;
}
