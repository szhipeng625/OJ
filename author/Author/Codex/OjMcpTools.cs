using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using OjCodex;

namespace author.CodexMcp;

/// <summary>
/// ACMDOGCodex 的 MCP 工具：把题库能力暴露给任意 MCP 客户端（Claude Code / 其他 Agent）。
///
/// 这里只做「协议适配」——真正的活儿全部转发给 <see cref="OjToolCatalog"/>，
/// 与出题端面板里跑的是同一份实现、同一套按题串行闸门，行为不会漂移。
/// </summary>
[McpServerToolType]
public sealed class OjMcpTools
{
    private static IOjToolHost? _host;

    /// <summary>由宿主（Author.exe --mcp）在启动时注入。</summary>
    public static void Attach(IOjToolHost host) => _host = host;

    private static IOjToolHost Require =>
        _host ?? throw new InvalidOperationException("题库未就绪：MCP 服务启动时未注入 IOjToolHost。");

    /// <summary>把工具入参转成 <see cref="OjToolCatalog"/> 认的字典形式，统一步进同一条派发路径。</summary>
    private static async Task<string> CallAsync(string name, Dictionary<string, JsonElement> input)
    {
        var outcome = await OjToolCatalog.InvokeAsync(Require, name, input);
        return outcome.IsError ? "{\"error\":" + JsonSerializer.Serialize(outcome.Result) + "}" : outcome.Result;
    }

    [McpServerTool(Name = OjToolCatalog.ListProblems)]
    [Description("列出 ACMDOG 题库全部题目（含未公开）：id / 标题 / 标签 / 是否公开 / 测试数据组数。")]
    public static Task<string> ListProblemsAsync(CancellationToken ct = default)
        => CallAsync(OjToolCatalog.ListProblems, []);

    [McpServerTool(Name = OjToolCatalog.GetProblem)]
    [Description("取一道题的全部内容：题面 Markdown、样例、时限内存、标签、标程源码、测试数据清单。")]
    public static Task<string> GetProblemAsync(
        [Description("题目编号，例如 113")] int id,
        CancellationToken ct = default)
        => CallAsync(OjToolCatalog.GetProblem, new() { ["id"] = JsonSerializer.SerializeToElement(id) });

    [McpServerTool(Name = OjToolCatalog.SaveStatement)]
    [Description("把题面写回题库（题面 / 样例 / 时限 / 内存 / 标签）。description 必须传完整正文，不是差异片段。")]
    public static Task<string> SaveStatementAsync(
        [Description("题目编号")] int id,
        [Description("题目标题")] string title,
        [Description("完整题面 Markdown 正文")] string description,
        [Description("样例输入（可空）")] string? sampleIn = null,
        [Description("样例输出（可空）")] string? sampleOut = null,
        [Description("时间限制毫秒（缺省沿用原值，传 0）")] int timeMs = 0,
        [Description("内存限制 MB（缺省沿用原值，传 0）")] int memMb = 0,
        CancellationToken ct = default)
    {
        var input = new Dictionary<string, JsonElement>
        {
            ["id"] = JsonSerializer.SerializeToElement(id),
            ["title"] = JsonSerializer.SerializeToElement(title),
            ["description"] = JsonSerializer.SerializeToElement(description),
        };
        if (sampleIn is not null) input["sample_in"] = JsonSerializer.SerializeToElement(sampleIn);
        if (sampleOut is not null) input["sample_out"] = JsonSerializer.SerializeToElement(sampleOut);
        if (timeMs > 0) input["time_ms"] = JsonSerializer.SerializeToElement(timeMs);
        if (memMb > 0) input["mem_mb"] = JsonSerializer.SerializeToElement(memMb);
        return CallAsync(OjToolCatalog.SaveStatement, input);
    }

    [McpServerTool(Name = OjToolCatalog.ListTags)]
    [Description("列出题库里已用过的全部标签（出现次数降序），用于新增标签时保持风格一致。")]
    public static Task<string> ListTagsAsync(CancellationToken ct = default)
        => CallAsync(OjToolCatalog.ListTags, []);

    [McpServerTool(Name = OjToolCatalog.GetStd)]
    [Description("读取某题标程（std）源码，用于核对题面描述与实现是否一致。")]
    public static Task<string> GetStdAsync(
        [Description("题目编号")] int id,
        CancellationToken ct = default)
        => CallAsync(OjToolCatalog.GetStd, new() { ["id"] = JsonSerializer.SerializeToElement(id) });

    [McpServerTool(Name = OjToolCatalog.CompileStd)]
    [Description("编译某题标程并返回编译输出。")]
    public static Task<string> CompileStdAsync(
        [Description("题目编号")] int id,
        CancellationToken ct = default)
        => CallAsync(OjToolCatalog.CompileStd, new() { ["id"] = JsonSerializer.SerializeToElement(id) });
}
