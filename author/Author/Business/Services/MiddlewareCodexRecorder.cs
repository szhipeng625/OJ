using System.Text.Json;
using author.DataAccess.Interop;
using OjCodex;

namespace author.Business.Services;

/// <summary>
/// 把 ACMDOGCodex 的用量与题面改动审计写进中间件。
///
/// 走 ojcore.dll（<see cref="OjCoreInterop"/>）而不是自己发 HTTP ——
/// 这样地址拼接、Bearer 令牌、自签证书处理全部与题目拉取共用 http_client 那一层，
/// 登录态也只有原生层一份，不会出现「令牌刷新了但某条路没跟上」这类问题。
///
/// 所有方法**不抛异常**：中间件没部署新接口、网络抖动，都只让审计静默丢失，
/// 绝不影响用户正在做的改题操作。
/// </summary>
public sealed class MiddlewareCodexRecorder : ICodexRecorder
{
    private static readonly JsonSerializerOptions Opt = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>最近一次失败原因（供界面提示「审计未落库」）。</summary>
    public string LastError { get; private set; } = "";

    public Task RecordRunAsync(CodexRunRecord r, CancellationToken ct = default)
    {
        Post(OjCoreInterop.CodexRecordRunJson(JsonSerializer.Serialize(new
        {
            runUid = r.RunUid,
            username = r.Username,
            model = r.Model,
            effort = r.Effort,
            instruction = Truncate(r.Instruction, 8000),
            toolCalls = r.ToolCalls,
            elapsedMs = r.ElapsedMs,
            ok = r.Ok,
            error = Truncate(r.Error, 2000),
            inputTokens = r.Usage.InputTokens,
            outputTokens = r.Usage.OutputTokens,
            cacheReadTokens = r.Usage.CacheReadTokens,
            cacheWriteTokens = r.Usage.CacheWriteTokens,
            contextTokens = r.ContextTokens,
            costUsd = r.CostUsd,
        }, Opt)));
        return Task.CompletedTask;
    }

    public Task RecordStatementAsync(CodexStatementAudit a, CancellationToken ct = default)
    {
        Post(OjCoreInterop.CodexRecordAuditJson(JsonSerializer.Serialize(new
        {
            runUid = a.RunUid,
            problemId = a.ProblemId,
            username = a.Username,
            beforeTitle = a.BeforeTitle,
            beforeDescription = a.BeforeDescription,
            afterTitle = a.AfterTitle,
            afterDescription = a.AfterDescription,
            tags = a.Tags,
            timeMs = a.TimeMs,
            memMb = a.MemMb,
            message = Truncate(a.Message, 4000),
        }, Opt)));
        return Task.CompletedTask;
    }

    // ---------- 读：审计查看窗口用 ----------

    public async Task<List<CodexRunRow>> FetchRunsAsync(int limit = 100, int problemId = 0, CancellationToken ct = default)
        => await GetAsync<List<CodexRunRow>>(() => OjCoreInterop.CodexListRunsJson(limit, problemId)) ?? [];

    public async Task<List<CodexAuditRow>> FetchAuditsAsync(string? runUid = null, int problemId = 0, int limit = 50, CancellationToken ct = default)
        => await GetAsync<List<CodexAuditRow>>(
            () => OjCoreInterop.CodexListAuditsJson(runUid ?? "", problemId, limit)) ?? [];

    // ---------- 内部 ----------

    /// <summary>写回结果是一个 {"ok":..} 信封；失败只记原因，不打断调用方。</summary>
    private void Post(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                LastError = "";
                return;
            }
            string err = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "未知错误";
            LastError = IsMissingEndpoint(err)
                ? "中间件还没有 /api/codex/* 接口（需要重新构建部署中间件），审计未落库。"
                : "审计未落库：" + err;
        }
        catch (Exception ex)
        {
            LastError = "审计上报失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 读接口返回空数组既可能是"没有数据"，也可能是"接口不存在"。
    /// 这里把两种情况合并成友好提示，避免用户对着空列表猜。
    /// </summary>
    private async Task<T?> GetAsync<T>(Func<string> fetch) where T : class
    {
        try
        {
            string json = await Task.Run(fetch);
            var result = JsonSerializer.Deserialize<T>(json, Opt);
            LastError = result is System.Collections.ICollection { Count: 0 }
                ? "没有数据（也可能是中间件还没有 /api/codex/* 接口 —— 需要重新构建部署中间件）。"
                : "";
            return result;
        }
        catch (Exception ex)
        {
            LastError = "读取审计失败：" + ex.Message;
            return null;
        }
    }

    private static bool IsMissingEndpoint(string err) =>
        err.Contains("404", StringComparison.Ordinal) ||
        err.Contains("Not Found", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}

/// <summary>一次运行（对应 codex_runs 一行）。</summary>
public sealed record CodexRunRow(
    string RunUid, string Username, string Model, string Effort, string Instruction,
    int ToolCalls, long ElapsedMs, bool Ok, string Error,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens,
    long ContextTokens, double CostUsd, string Ts, int AuditCount)
{
    public double CacheHitRate
    {
        get
        {
            long total = InputTokens + CacheReadTokens + CacheWriteTokens;
            return total <= 0 ? 0 : (double)CacheReadTokens / total;
        }
    }

    public string Header => $"{Ts}  {Username}  {Model}";

    public string Summary =>
        $"工具 {ToolCalls} 次 · 输出 {OutputTokens:N0} tok · 缓存读 {CacheReadTokens:N0} · 命中率 {CacheHitRate:P0} · ${CostUsd:0.0000}"
        + (AuditCount > 0 ? $" · 改动 {AuditCount} 次" : "")
        + (Ok ? "" : " · 失败：" + Error);
}

/// <summary>一次题面改动（对应 codex_statement_audits 一行）。</summary>
public sealed record CodexAuditRow(
    long Id, string RunUid, int ProblemId, string Username,
    string BeforeTitle, string BeforeDescription, string AfterTitle, string AfterDescription,
    string Tags, int TimeMs, int MemMb, string Message, string Ts)
{
    public string Header => $"P{ProblemId}  {Ts}";
}
