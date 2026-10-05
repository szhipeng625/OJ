using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace OjCodex;

/// <summary>
/// ACMDOGCodex 的 agent 主循环：一次用户指令 → 若干轮「模型思考 → 调工具 → 回结果」→ 一段回答。
///
/// 降本提速的着力点（按收益排序）：
///   1. 会话级缓存复用：整段对话留在 <see cref="_messages"/> 里跨指令累积。第二条指令
///      只需在尾部追加，前面整段前缀走缓存读 —— 这是命中率的主要来源。
///   2. 缓存预热：max_tokens=0 的空请求，把 tools+system 前缀先写进缓存，
///      用户点发送时首包即命中。总写入次数不变，只是把等待挪到用户打字期间。
///   3. 并行工具：同一轮的多个 tool_use 并发执行，少几个 RTT。
///   4. 上下文裁剪：超长会话按「整对」裁掉最早的往返，避免上下文线性膨胀。
///   5. 轮次与预算护栏：MaxToolRounds / RunOutputTokenBudget 卡死失控循环。
/// </summary>
public sealed class CodexAgent
{
    private readonly AnthropicClient _client;
    private readonly CodexSettings _settings;

    /// <summary>持久会话：跨指令保留，是缓存命中的前提。</summary>
    private readonly List<MessageParam> _messages = [];

    /// <summary>题库的向量索引。实例级持有 —— 索引与查询缓存都靠它跨指令复用。</summary>
    private readonly CodexVectorIndex<ProblemSearchDoc> _index = new(d => d.Text);

    private int? _sessionProblemId;
    private bool _sessionSeeded;

    /// <summary>本地题库访问（面板与 MCP 共用同一份实现）。</summary>
    public IOjToolHost Host { get; }

    /// <summary>审计与用量落库出口；为空则只跑不记（MCP 模式、未登录等都走这条）。</summary>
    public ICodexRecorder? Recorder { get; set; }

    /// <summary>记录改动归属用的用户名。</summary>
    public string Username { get; set; } = "";

    /// <summary>最近一次运行的关联 id（审计记录用）。</summary>
    public string LastRunUid { get; private set; } = "";

    private readonly Stopwatch _runClock = new();
    private string _currentInstruction = "";

    /// <summary>最近一次任务的累计用量。</summary>
    public CodexUsage RunUsage { get; private set; } = CodexUsage.Zero;

    /// <summary>最近一次请求的完整提示词 token 数（输入 + 缓存读 + 缓存写），即当前上下文大小。</summary>
    public long LastContextTokens { get; private set; }

    /// <summary>当前会话里的消息条数。</summary>
    public int SessionMessageCount => _messages.Count;

    /// <summary>是否有可续用的会话（决定"新会话"按钮是否可点）。</summary>
    public bool HasSession => _messages.Count > 0;

    /// <summary>每产生一步（思考/正文/工具调用/工具结果）回调一次，供看板实时展示。</summary>
    public event Action<CodexStep>? StepEmitted;

    /// <summary>每次 API 调用后回调当次的用量增量（会话累计由调用方自己维护）。</summary>
    public event Action<CodexUsage>? UsageUpdated;

    public CodexAgent(CodexSettings settings, IOjToolHost host)
    {
        _settings = settings;
        Host = host;

        // 自定义网关走 SDK 认的 ANTHROPIC_BASE_URL（进程级，构造客户端前设好）
        if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
            Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", settings.BaseUrl);

        _client = string.IsNullOrWhiteSpace(settings.EffectiveApiKey)
            ? new AnthropicClient()
            : new AnthropicClient { ApiKey = settings.EffectiveApiKey };
    }

    private void Emit(CodexStepKind kind, string text, string toolName = "", bool isError = false)
        => StepEmitted?.Invoke(new CodexStep { Kind = kind, Text = text, ToolName = toolName, IsError = isError });

    /// <summary>丢弃当前会话，下一条指令从头开始（会重新写一次缓存）。</summary>
    public void ResetSession()
    {
        _messages.Clear();
        _sessionProblemId = null;
        _sessionSeeded = false;
        LastContextTokens = 0;
    }

    // ---------- 缓存预热 ----------

    /// <summary>
    /// 预热 tools + system 前缀。用 max_tokens=0 不产生输出，只把前缀写进缓存。
    /// 失败不影响正常使用（比如服务端不支持 0 输出、网络抖动），返回 false 即可。
    /// </summary>
    public async Task<bool> WarmCacheAsync(IReadOnlyList<ProblemBrief> bank, CancellationToken ct = default)
    {
        if (!_settings.IsConfigured || !_settings.EnablePromptCaching) return false;

        MessageCreateParams Build(int maxTokens) => new()
        {
            Model = _settings.Model,
            MaxTokens = maxTokens,
            System = BuildSystem(bank),
            Tools = OjToolCatalog.BuildDefinitions(),
            Messages = [new() { Role = Role.User, Content = "(warm)" }],
            CacheControl = CacheControl(),
        };

        // 官方 Anthropic 支持 max_tokens=0（零输出，纯写缓存）；
        // 有些兼容端点（如 DeepSeek 的 anthropic 端点）要求 ≥1，那就退到 1 —— 代价是一个输出 token。
        try
        {
            await _client.Messages.Create(Build(0), ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            try
            {
                await _client.Messages.Create(Build(1), ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;   // 预热是锦上添花，失败就照常走冷启动
            }
        }
    }

    // ---------- 上下文计量 ----------

    /// <summary>用 count_tokens 精确测当前上下文（含 tools / system / 历史）。失败返回 null。</summary>
    public async Task<long?> CountContextTokensAsync(IReadOnlyList<ProblemBrief> bank)
    {
        if (!_settings.IsConfigured) return null;
        try
        {
            var msgs = _messages.Count > 0
                ? _messages
                : new List<MessageParam> { new() { Role = Role.User, Content = "(空会话)" } };

            var r = await _client.Messages.CountTokens(new MessageCountTokensParams
            {
                Model = _settings.Model,
                System = BuildSystem(bank),
                Messages = msgs,
            });
            return r.InputTokens;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 主循环 ----------

    /// <summary>
    /// 跑一轮任务。bank 是题库快照（进稳定块 2）；focus 是当前聚焦题目的正文（进会话首条消息）。
    /// 同一道题的后续指令只追加一条 user 消息，前面整段历史走缓存读。
    /// </summary>
    public async Task<CodexRunResult> RunAsync(
        string instruction,
        ProblemDoc? focus,
        IReadOnlyList<ProblemBrief> bank,
        CancellationToken ct = default)
    {
        if (!_settings.IsConfigured)
            return new CodexRunResult(false, "", CodexUsage.Zero, 0,
                "未配置 Anthropic API Key。请在 config.xml 的 <codex><apiKey> 里填写，或设置环境变量 ANTHROPIC_API_KEY。");

        // 换题就开新会话：历史里是另一道题的上下文，既没用也会拖累缓存
        if (_sessionSeeded && _sessionProblemId != focus?.Id)
        {
            Emit(CodexStepKind.Info, "聚焦题目已切换，开启新会话。");
            ResetSession();
        }
        _sessionProblemId = focus?.Id;

        // 每次运行一个关联 id：审计记录和用量账都挂它，不依赖服务端自增 id 的时序
        LastRunUid = Guid.NewGuid().ToString("N");
        _currentInstruction = instruction;
        _runClock.Restart();

        var sw = Stopwatch.StartNew();
        CodexUsage runUsage = CodexUsage.Zero;
        RunUsage = CodexUsage.Zero;
        int toolCalls = 0;

        var system = BuildSystem(bank);
        var tools = OjToolCatalog.BuildDefinitions();

        // 向量检索：挑出与本次指令最相关的题目，避免让模型先兜一圈 list + get
        var retrieved = await RetrieveAsync(instruction, focus, bank, ct);

        // 会话首条消息带题目正文；后续指令只追加指令本身 + 本次检索结果
        // （检索结果随指令变，只能进 user 消息，进 system 会把缓存前缀打没）
        string userText = _sessionSeeded
            ? PrependRetrieved(instruction, retrieved)
            : CodexSystemPrompt.BuildTaskMessage(instruction, focus, retrieved, _settings.RetrievalSnippetChars);

        _messages.Add(new MessageParam { Role = Role.User, Content = userText });
        _sessionSeeded = true;
        TrimContext();

        Emit(CodexStepKind.Info,
            $"任务开始（模型 {_settings.Model} / effort {_settings.Effort} / 题库 {bank.Count} 题 / " +
            $"会话 {_messages.Count} 条消息）");

        try
        {
            for (int round = 0; round < Math.Max(1, _settings.MaxToolRounds); round++)
            {
                if (_settings.RunOutputTokenBudget > 0 && runUsage.OutputTokens >= _settings.RunOutputTokenBudget)
                {
                    Emit(CodexStepKind.Info, $"已达本次任务输出预算 {_settings.RunOutputTokenBudget} token，停止继续调用。");
                    return Result(true, "（已达 token 预算，任务提前结束）", runUsage, toolCalls);
                }

                var response = await _client.Messages.Create(BuildRequest(system, tools, _messages), ct);

                var usage = new CodexUsage(
                    response.Usage.InputTokens,
                    response.Usage.OutputTokens,
                    response.Usage.CacheReadInputTokens ?? 0,
                    response.Usage.CacheCreationInputTokens ?? 0);
                runUsage = runUsage.Plus(usage);
                RunUsage = runUsage;
                LastContextTokens = usage.TotalInputTokens;   // 本次提示词的实际大小 = 当前上下文
                UsageUpdated?.Invoke(usage);

                Emit(CodexStepKind.Info,
                    $"第 {round + 1} 轮：上下文 {LastContextTokens:N0} tok（输入 {usage.InputTokens:N0} / " +
                    $"缓存读 {usage.CacheReadTokens:N0} / 缓存写 {usage.CacheWriteTokens:N0}）· " +
                    $"输出 {usage.OutputTokens:N0} · 命中率 {usage.CacheHitRate:P0}");

                if (response.StopReason == "refusal")
                {
                    string why = response.StopDetails is { } d ? $"{d.Category}：{d.Explanation}" : "模型拒绝了本次请求";
                    Emit(CodexStepKind.Info, "请求被拒绝：" + why, isError: true);
                    return Result(false, "", runUsage, toolCalls, why);
                }

                // ---- 拆解这一轮的输出 ----
                var assistantContent = new List<ContentBlockParam>();
                var toolUses = new List<ToolUseBlock>();
                var answerText = new StringBuilder();

                foreach (var block in response.Content)
                {
                    if (block.TryPickThinking(out ThinkingBlock? thinking))
                    {
                        if (_settings.ShowThinking && !string.IsNullOrWhiteSpace(thinking.Thinking))
                            Emit(CodexStepKind.Thinking, thinking.Thinking);
                        // 思考块必须原样回传（签名不能动），否则后续轮次会被拒
                        assistantContent.Add(new ThinkingBlockParam
                        {
                            Thinking = thinking.Thinking,
                            Signature = thinking.Signature,
                        });
                    }
                    else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                    {
                        assistantContent.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickText(out TextBlock? text))
                    {
                        assistantContent.Add(new TextBlockParam { Text = text.Text });
                        answerText.Append(text.Text);
                        Emit(CodexStepKind.Text, text.Text);
                    }
                    else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                    {
                        assistantContent.Add(new ToolUseBlockParam
                        {
                            ID = toolUse.ID,
                            Name = toolUse.Name,
                            Input = toolUse.Input,
                        });
                        toolUses.Add(toolUse);   // 执行在下一段统一处理（并发 + 记录审计）
                    }
                }

                if (response.StopReason != "tool_use" || toolUses.Count == 0)
                {
                    sw.Stop();
                    Emit(CodexStepKind.Info, $"任务结束，用时 {sw.Elapsed.TotalSeconds:F1}s，共 {toolCalls} 次工具调用");
                    return Result(true, answerText.ToString().Trim(), runUsage, toolCalls);
                }

                // ---- 执行工具（同一轮的多个调用并发跑，各自仍走按题串行闸门）----
                toolCalls += toolUses.Count;
                foreach (var tu in toolUses)
                    Emit(CodexStepKind.ToolCall, DescribeToolUse(tu.Name, tu.Input), tu.Name);

                var outcomes = await RunToolsAsync(toolUses, ct);

                var toolResults = new List<ContentBlockParam>();
                for (int i = 0; i < toolUses.Count; i++)
                {
                    var outcome = outcomes[i];
                    Emit(CodexStepKind.ToolResult, Truncate(outcome.Result, 1200), toolUses[i].Name, outcome.IsError);
                    toolResults.Add(new ToolResultBlockParam
                    {
                        ToolUseID = toolUses[i].ID,
                        Content = Truncate(outcome.Result, Math.Max(500, _settings.MaxToolResultChars)),
                        IsError = outcome.IsError,
                    });
                }

                _messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
                _messages.Add(new MessageParam { Role = Role.User, Content = toolResults });
                TrimContext();
            }

            Emit(CodexStepKind.Info, $"已达最大轮次 {_settings.MaxToolRounds}，停止。");
            return Result(true, "（已达最大工具轮次上限，任务提前结束）", runUsage, toolCalls);
        }
        catch (OperationCanceledException)
        {
            Emit(CodexStepKind.Info, "任务被取消");
            return Result(false, "", runUsage, toolCalls, "已取消");
        }
        catch (Exception ex)
        {
            Emit(CodexStepKind.Info, "调用失败：" + ex.Message, isError: true);
            return Result(false, "", runUsage, toolCalls, ex.Message);
        }
    }

    /// <summary>
    /// 向量分解 + 检索：把题库文档拆成 TF-IDF 稀疏向量，用指令算余弦取 top-K。
    /// 题库规模不超过 topK 时直接跳过 —— 全量已经在 system 的 problem_bank 里，检索纯属白算。
    /// </summary>
    private async Task<IReadOnlyList<ProblemSearchDoc>> RetrieveAsync(
        string instruction, ProblemDoc? focus, IReadOnlyList<ProblemBrief> bank, CancellationToken ct)
    {
        if (!_settings.EnableRetrieval) return [];
        if (bank.Count <= Math.Max(1, _settings.RetrievalTopK)) return [];

        try
        {
            var docs = await Host.GetSearchDocumentsAsync(ct);
            var items = bank
                .Select(b => new ProblemSearchDoc(b,
                    docs.TryGetValue(b.Id, out var t) && !string.IsNullOrWhiteSpace(t)
                        ? t
                        : $"P{b.Id} {b.Title}"))
                .ToList();

            _index.EnsureBuilt(items);

            // 查询 = 指令 + 聚焦题目的标题/标签，让「改这道题」也能带上题目自身的语义
            string query = focus is null
                ? instruction
                : $"{instruction} {focus.Title} {string.Join(' ', focus.Tags)}";

            var hits = _index.Search(query, _settings.RetrievalTopK);
            var result = hits.Select(h => h.Item).ToList();

            // 指令里点名了题号（P127 / #127 / 127）时必须出现在结果里：
            // 向量对短数字不敏感（实测 P127 只拿到 0.15 分），但用户点名的题目漏掉就是帮倒忙。
            var named = ExtractNamedProblems(instruction, bank);
            if (named.Count > 0)
            {
                result = named
                    .Concat(result.Where(r => named.All(n => n.Brief.Id != r.Brief.Id)))
                    .Take(Math.Max(1, _settings.RetrievalTopK))
                    .ToList();
            }

            if (result.Count > 0)
                Emit(CodexStepKind.Info,
                    $"向量检索：题库 {bank.Count} 题 → 命中 {result.Count} 题" +
                    (named.Count > 0 ? $"（其中 {named.Count} 题由指令点名强制入选）" : "") +
                    $"（top1 {result[0].Brief.Title}）");
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Emit(CodexStepKind.Info, "向量检索失败，退回全量索引：" + ex.Message);
            return [];
        }
    }

    /// <summary>从指令里抽出被点名的题号（P127 / #127 / 裸数字），并映射回题库文档。</summary>
    private static List<ProblemSearchDoc> ExtractNamedProblems(string instruction, IReadOnlyList<ProblemBrief> bank)
    {
        var found = new List<ProblemSearchDoc>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(instruction, @"(?<![0-9])(?:[Pp]#?|#)?([0-9]{2,6})(?![0-9])"))
        {
            if (!int.TryParse(m.Groups[1].Value, out int id)) continue;
            var brief = bank.FirstOrDefault(b => b.Id == id);
            if (brief is null) continue;
            if (found.Any(f => f.Brief.Id == id)) continue;
            found.Add(new ProblemSearchDoc(brief, $"P{brief.Id} {brief.Title} {string.Join(' ', brief.Tags)}"));
        }
        return found;
    }

    private string PrependRetrieved(string instruction, IReadOnlyList<ProblemSearchDoc> retrieved)
    {
        if (retrieved.Count == 0) return instruction;
        return CodexSystemPrompt.BuildRetrievedContext(retrieved, _settings.RetrievalSnippetChars)
             + Environment.NewLine + instruction;
    }

    private async Task<CodexToolOutcome[]> RunToolsAsync(List<ToolUseBlock> toolUses, CancellationToken ct)
    {
        // 审计上下文：写回题面时把「谁、哪次运行」一起记下来
        var audit = Recorder is null
            ? null
            : new OjToolCatalog.StatementAuditContext(Recorder, LastRunUid, Username);

        if (!_settings.UseParallelTools || toolUses.Count == 1)
        {
            var serial = new CodexToolOutcome[toolUses.Count];
            for (int i = 0; i < toolUses.Count; i++)
                serial[i] = await OjToolCatalog.InvokeAsync(Host, toolUses[i].Name, toolUses[i].Input, ct, audit);
            return serial;
        }

        // 并发：工具之间没有依赖（有依赖会表现为分轮调用）。
        // 取结果时保持与 tool_use 相同的顺序，避免与 tool_result 的配对错位。
        var tasks = toolUses.Select(tu => OjToolCatalog.InvokeAsync(Host, tu.Name, tu.Input, ct, audit)).ToArray();
        return await Task.WhenAll(tasks);
    }

    private CodexRunResult Result(bool ok, string answer, CodexUsage usage, int toolCalls, string error = "")
    {
        var result = new CodexRunResult(ok, answer, usage, toolCalls, error, LastContextTokens, _messages.Count);
        RecordRunSafe(result);
        return result;
    }

    /// <summary>
    /// 落一条用量账。fire-and-forget 且有兜底 —— 记账是旁路，
    /// 既不能拖慢返回，更不能因为中间件不可用而让用户的操作失败。
    /// </summary>
    private void RecordRunSafe(CodexRunResult result)
    {
        if (Recorder is not { } recorder) return;

        var record = new CodexRunRecord(
            LastRunUid, Username, _settings.Model, _settings.Effort, _currentInstruction,
            result.ToolCallCount, _runClock.ElapsedMilliseconds, result.Ok, result.Error,
            result.Usage, result.ContextTokens, CodexPricing.Default.Estimate(result.Usage));

        _ = Task.Run(async () =>
        {
            try { await recorder.RecordRunAsync(record); }
            catch { /* 旁路失败静默：不打扰用户 */ }
        });
    }

    /// <summary>
    /// 上下文裁剪：只裁掉最早的那些「助手回复 + 工具结果」整对，保留首条任务消息与最近的往返。
    /// 首条消息绝不能删 —— 它带着题目正文，删了模型就得重新调工具去读。
    /// </summary>
    private void TrimContext()
    {
        int max = Math.Max(4, _settings.MaxContextMessages);
        if (_messages.Count <= max) return;

        int remove = _messages.Count - max;
        if (remove % 2 != 0) remove++;                 // 成对删除，维持 (assistant, user) 交替
        remove = Math.Min(remove, _messages.Count - 2);
        if (remove <= 0) return;

        _messages.RemoveRange(1, remove);
        Emit(CodexStepKind.Info,
            $"上下文超过 {max} 条，已裁掉最早 {remove} 条历史往返（保留任务说明与最近往返）。" +
            "注意：这次裁剪会让消息尾部的缓存失效一次，system 前缀不受影响。");
    }

    private List<TextBlockParam> BuildSystem(IReadOnlyList<ProblemBrief> bank) =>
    [
        new()
        {
            Text = CodexSystemPrompt.BuildStable(_settings),
            CacheControl = CacheControl(),
        },
        new()
        {
            Text = CodexSystemPrompt.BuildBankSnapshot(bank),
            CacheControl = CacheControl(),
        },
    ];

    private MessageCreateParams BuildRequest(
        List<TextBlockParam> system, List<ToolUnion> tools, List<MessageParam> messages)
    {
        return new MessageCreateParams
        {
            Model = _settings.Model,
            MaxTokens = Math.Clamp(_settings.MaxTokens, 1024, 128000),
            System = system,
            Tools = tools,
            Messages = messages,
            Thinking = new ThinkingConfigAdaptive
            {
                // 默认 display=omitted（思考正文为空）；要看思考就显式开 summarized
                Display = _settings.ShowThinking ? Display.Summarized : Display.Omitted,
            },
            OutputConfig = new OutputConfig { Effort = MapEffort(_settings.Effort) },
            // 顶层自动缓存：在最后一个可缓存块上补一个断点，
            // 让「历史消息」也能跨轮命中，而不是每轮都全量重算。
            CacheControl = _settings.EnablePromptCaching ? CacheControl() : null,
        };
    }

    private CacheControlEphemeral? CacheControl() =>
        _settings.EnablePromptCaching
            ? new CacheControlEphemeral { Ttl = _settings.CacheTtl == "1h" ? Ttl.Ttl1h : Ttl.Ttl5m }
            : null;

    private static Effort MapEffort(string effort) => effort?.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "max" => Effort.Max,
        _ => Effort.High,
    };

    private static string DescribeToolUse(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (input.Count == 0) return $"{name}()";
        return $"{name}({string.Join(", ", input.Select(kv => $"{kv.Key}={kv.Value}"))})";
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…（已截断）";
}
