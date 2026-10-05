using System.Text.Json;
using Anthropic.Models.Messages;

namespace OjCodex;

/// <summary>一次工具调用的结果（面板/看板/MCP 日志共用）。</summary>
public sealed record CodexToolOutcome(string Name, string InputJson, string Result, bool IsError);

/// <summary>
/// ACMDOGCodex 的题库工具集：定义（给模型看的 JSON Schema）+ 派发（落到 <see cref="IOjToolHost"/>）。
///
/// 定义顺序与文案必须稳定 —— 它们在 prompt cache 前缀的最前面（渲染顺序 tools → system → messages），
/// 任何一处改动都会让整段缓存失效。加工具时把它挂在列表末尾，别插队。
/// </summary>
public static class OjToolCatalog
{
    public const string ListProblems = "oj_list_problems";
    public const string GetProblem = "oj_get_problem";
    public const string SaveStatement = "oj_save_statement";
    public const string ListTags = "oj_list_tags";
    public const string GetStd = "oj_get_std";
    public const string CompileStd = "oj_compile_std";

    private static JsonElement Schema(string type, string description) =>
        JsonSerializer.SerializeToElement(new { type, description });

    /// <summary>工具定义清单（顺序 = 缓存前缀的一部分，别随意调整）。</summary>
    public static List<ToolUnion> BuildDefinitions() =>
    [
        new Tool
        {
            Name = ListProblems,
            Description = "列出题库全部题目（含未公开）。返回 id / 标题 / 标签 / 是否公开 / 数据组数。"
                        + "用于先看清题库现状，再决定改哪一题、标签怎么保持一致的风格。",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },
        new Tool
        {
            Name = GetProblem,
            Description = "取一道题的完整内容：题面 Markdown、样例输入输出、时限内存、标签、标程源码、测试数据文件清单。"
                        + "改写题面前必须先调它，不要在没看过原文的情况下动笔。",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["id"] = Schema("integer", "题目编号，例如 113"),
                },
                Required = ["id"],
            },
        },
        new Tool
        {
            Name = SaveStatement,
            Description = "把题面写回题库（题面/样例/时限/内存/标签一并保存）。这是唯一会改动画面的工具。"
                        + "务必传完整的题面正文，而不是差异片段；只改需要改的部分，其余原样保留。"
                        + "不带参数调用且 id=0 时只返回当前题面，不写回 —— 不做无意义的空写。",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["id"] = Schema("integer", "题目编号"),
                    ["title"] = Schema("string", "题目标题"),
                    ["description"] = Schema("string", "完整题面 Markdown 正文"),
                    ["sample_in"] = Schema("string", "样例输入（无则传空串）"),
                    ["sample_out"] = Schema("string", "样例输出（无则传空串）"),
                    ["time_ms"] = Schema("integer", "时间限制毫秒，缺省沿用原值"),
                    ["mem_mb"] = Schema("integer", "内存限制 MB，缺省沿用原值"),
                    ["tags"] = Schema("array", "标签数组，尽量复用题库已有标签"),
                },
                Required = ["id", "title", "description"],
            },
        },
        new Tool
        {
            Name = ListTags,
            Description = "列出题库里已经用过的全部标签，并给出出现次数。新增标签前先看这里，避免同义标签满天飞。",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },
        new Tool
        {
            Name = GetStd,
            Description = "读取某题的标程（std）源码。用于核对题面描述与标程实现是否一致（例如数据范围、边界处理）。",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["id"] = Schema("integer", "题目编号"),
                },
                Required = ["id"],
            },
        },
        new Tool
        {
            Name = CompileStd,
            Description = "编译某题的标程并返回编译输出。改完题面后如果动了标程，用它确认还能编过。",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["id"] = Schema("integer", "题目编号"),
                },
                Required = ["id"],
            },
        },
    ];

    /// <summary>工具名清单（MCP 注册与面板展示用）。</summary>
    public static IReadOnlyList<string> Names =>
    [
        ListProblems, GetProblem, SaveStatement, ListTags, GetStd, CompileStd,
    ];

    /// <summary>
    /// 把一次 tool_use 派发到题库；任何异常都转成 is_error 结果回给模型，不往上抛。
    /// <paramref name="audit"/> 非空时，写回题面会顺带记一条改动审计（前 → 后）。
    /// </summary>
    public static async Task<CodexToolOutcome> InvokeAsync(
        IOjToolHost host, string name, IReadOnlyDictionary<string, JsonElement> input,
        CancellationToken ct = default, StatementAuditContext? audit = null)
    {
        string inputJson = JsonSerializer.Serialize(input);
        try
        {
            string result = name switch
            {
                ListProblems => await ListProblemsAsync(host, ct),
                GetProblem => await GetProblemAsync(host, input, ct),
                SaveStatement => await SaveStatementAsync(host, input, audit, ct),
                ListTags => await ListTagsAsync(host, ct),
                GetStd => await GetStdAsync(host, input, ct),
                CompileStd => await CompileStdAsync(host, input, ct),
                _ => throw new InvalidOperationException($"未知工具：{name}"),
            };
            return new CodexToolOutcome(name, inputJson, result, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CodexToolOutcome(name, inputJson, "工具执行失败：" + ex.Message, true);
        }
    }

    // ---------- 各工具实现 ----------

    private static async Task<string> ListProblemsAsync(IOjToolHost host, CancellationToken ct)
    {
        var list = await host.ListProblemsAsync(ct);
        var rows = list.OrderBy(p => p.Id).Select(p => new
        {
            id = p.Id,
            title = p.Title,
            @public = p.IsPublic,
            tags = p.Tags,
            dataCount = p.DataCount,
        });
        return JsonSerializer.Serialize(new { count = list.Count, problems = rows });
    }

    private static async Task<string> GetProblemAsync(
        IOjToolHost host, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        int id = GetInt(input, "id");
        var doc = await host.GetProblemAsync(id, ct);
        if (doc is null) return $"{{\"error\":\"题目 P{id} 不存在\"}}";

        return JsonSerializer.Serialize(new
        {
            id = doc.Id,
            title = doc.Title,
            description = doc.Description,
            sampleIn = doc.SampleIn,
            sampleOut = doc.SampleOut,
            timeMs = doc.TimeMs,
            memMb = doc.MemMb,
            tags = doc.Tags,
            dataFiles = doc.DataFiles,
            judgeMode = doc.JudgeMode,
            stdCode = doc.StdCode,
        });
    }

    /// <summary>写回题面时要记的审计上下文（谁、哪次运行）。</summary>
    public sealed record StatementAuditContext(ICodexRecorder? Recorder, string RunUid, string Username);

    private static async Task<string> SaveStatementAsync(
        IOjToolHost host, IReadOnlyDictionary<string, JsonElement> input,
        StatementAuditContext? audit, CancellationToken ct)
    {
        int id = GetInt(input, "id");
        if (id <= 0) return "{\"error\":\"id 非法\"}";

        // 只传了 id 时不写回，直接回当前题面，避免"空写"污染数据
        if (!input.ContainsKey("description"))
        {
            var cur = await host.GetProblemAsync(id, ct);
            return cur is null
                ? $"{{\"error\":\"题目 P{id} 不存在\"}}"
                : JsonSerializer.Serialize(new { note = "未提供 description，未写回", current = cur.Description });
        }

        var existing = await host.GetProblemAsync(id, ct);
        string title = GetString(input, "title", existing?.Title ?? "");
        string description = GetString(input, "description", existing?.Description ?? "");
        string sampleIn = GetString(input, "sample_in", existing?.SampleIn ?? "");
        string sampleOut = GetString(input, "sample_out", existing?.SampleOut ?? "");
        int timeMs = GetInt(input, "time_ms", existing?.TimeMs ?? 1000);
        int memMb = GetInt(input, "mem_mb", existing?.MemMb ?? 256);
        string[] tags = GetStrings(input, "tags", existing?.Tags ?? []);

        string message = await host.SaveStatementAsync(id, title, description, sampleIn, sampleOut, timeMs, memMb, tags, ct);

        // 审计：记下「改成什么样」和「改之前是什么样」。
        // 记录失败不影响写回结果 —— 审计是旁路，绝不能拖累改题本身。
        if (audit?.Recorder is { } recorder)
        {
            try
            {
                await recorder.RecordStatementAsync(new CodexStatementAudit(
                    audit.RunUid, id, audit.Username,
                    existing?.Title ?? "", existing?.Description ?? "",
                    title, description,
                    string.Join(",", tags), timeMs, memMb, message), ct);
            }
            catch { /* 旁路失败静默 */ }
        }

        return JsonSerializer.Serialize(new { ok = true, id, title, tags, message });
    }

    private static async Task<string> ListTagsAsync(IOjToolHost host, CancellationToken ct)
    {
        var tags = await host.ListTagsAsync(ct);
        return JsonSerializer.Serialize(new { count = tags.Count, tags });
    }

    private static async Task<string> GetStdAsync(
        IOjToolHost host, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        int id = GetInt(input, "id");
        string code = await host.GetStdAsync(id, ct);
        return string.IsNullOrEmpty(code)
            ? $"{{\"id\":{id},\"std\":\"\",\"note\":\"该题暂无标程\"}}"
            : JsonSerializer.Serialize(new { id, std = code });
    }

    private static async Task<string> CompileStdAsync(
        IOjToolHost host, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        int id = GetInt(input, "id");
        string output = await host.CompileStdAsync(id, ct);
        return JsonSerializer.Serialize(new { id, output });
    }

    // ---------- JsonElement 取值助手 ----------

    private static int GetInt(IReadOnlyDictionary<string, JsonElement> input, string key, int fallback = 0)
    {
        if (!input.TryGetValue(key, out var e)) return fallback;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out var n) ? n : fallback,
            JsonValueKind.String => int.TryParse(e.GetString(), out var s) ? s : fallback,
            _ => fallback,
        };
    }

    private static string GetString(IReadOnlyDictionary<string, JsonElement> input, string key, string fallback)
    {
        if (!input.TryGetValue(key, out var e)) return fallback;
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? fallback : fallback;
    }

    private static string[] GetStrings(IReadOnlyDictionary<string, JsonElement> input, string key, string[] fallback)
    {
        if (!input.TryGetValue(key, out var e)) return fallback;
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString();
            return string.IsNullOrWhiteSpace(s)
                ? []
                : s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        if (e.ValueKind != JsonValueKind.Array) return fallback;

        var list = new List<string>();
        foreach (var item in e.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } v)
                list.Add(v);
        return list.ToArray();
    }
}
