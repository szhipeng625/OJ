using OjCodex;

namespace author.Business.Services;

/// <summary>
/// <see cref="IOjToolHost"/> 在出题端的实现：把 Codex 的工具调用落到既有业务层（ProblemService / AuthorClient）。
///
/// 面板里的对话和 MCP 服务共用这一个实现，所以「AI 能改的题面」和「MCP 客户端能改的题面」
/// 走的是同一条路径、同一套并发闸门（BuildService 的按题串行），不会各写一套。
/// </summary>
public sealed class AuthorOjToolHost : IOjToolHost
{
    private readonly Workbench _wb;

    // 题库快照缓存：每次跑任务都要拿一次，避免高频重复读本地 meta
    private IReadOnlyList<ProblemBrief>? _bankCache;
    private DateTime _bankCacheAt = DateTime.MinValue;
    private static readonly TimeSpan BankCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>题面被写回后触发（参数为题目编号），供启动器刷新列表、工作台提示重新载入。</summary>
    public event Action<int>? ProblemChanged;

    public AuthorOjToolHost(Workbench wb) => _wb = wb;

    public async Task<IReadOnlyList<ProblemBrief>> ListProblemsAsync(CancellationToken ct = default)
    {
        if (_bankCache is { } cached && DateTime.Now - _bankCacheAt < BankCacheTtl)
            return cached;

        var list = await _wb.Problems.ListAsync();
        var briefs = new List<ProblemBrief>(list.Count);
        foreach (var p in list)
        {
            ct.ThrowIfCancellationRequested();
            string[] tags = [];
            try { tags = await _wb.Problems.GetTagsAsync(p.Id); } catch { /* 读不到标签不算错 */ }
            briefs.Add(new ProblemBrief(p.Id, p.Title, p.IsPublic, tags, p.DataCount));
        }

        _bankCache = briefs;
        _bankCacheAt = DateTime.Now;
        return briefs;
    }

    /// <summary>题目变动后让题库快照失效（新建/保存/发布时调用）。</summary>
    public void InvalidateBankCache()
    {
        _bankCache = null;
        _bankCacheAt = DateTime.MinValue;
    }

    public async Task<ProblemDoc?> GetProblemAsync(int id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var c = await _wb.Problems.LoadAsync(id);
        if (c is null) return null;

        string[] data;
        try { data = (await _wb.Problems.ListDataAsync(id)).Select(d => d.Display).ToArray(); }
        catch { data = []; }

        string std = c.StdCode ?? "";
        return new ProblemDoc(
            id,
            c.StatementTitle ?? "",
            c.StatementDesc ?? "",
            c.SampleIn ?? "",
            c.SampleOut ?? "",
            c.Meta.TimeLimitMs,
            c.Meta.MemLimitMB,
            c.Meta.Tags ?? [],
            std,
            data,
            0);
    }

    public async Task<string> SaveStatementAsync(int id, string title, string description,
        string sampleIn, string sampleOut, int timeMs, int memMb, string[] tags,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = await _wb.Problems.SaveStatementAsync(id, title, description, sampleIn, sampleOut, timeMs, memMb, tags);
        InvalidateBankCache();

        // 改动可能影响本地副本，通知订阅方（启动器）刷新列表
        if (result.Ok)
        {
            try { ProblemChanged?.Invoke(id); } catch { /* 刷新失败不影响写回结果 */ }
        }
        return result.Message;
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _wb.Problems.ListAllTagsAsync();
    }

    public async Task<string> GetStdAsync(int id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var c = await _wb.Problems.LoadAsync(id);
        return c?.StdCode ?? "";
    }

    /// <summary>
    /// 建检索索引用的可搜索文本。全部走本地：题库轻量索引 + 本地工作副本的题面片段，
    /// 不做任何中间件往返（这个接口每次指令前都会调）。
    /// </summary>
    public async Task<IReadOnlyDictionary<int, string>> GetSearchDocumentsAsync(CancellationToken ct = default)
    {
        var briefs = await ListProblemsAsync(ct);
        var map = new Dictionary<int, string>(briefs.Count);

        foreach (var b in briefs)
        {
            ct.ThrowIfCancellationRequested();
            var sb = new System.Text.StringBuilder();
            sb.Append('P').Append(b.Id).Append(' ').Append(b.Title);
            if (b.Tags.Length > 0) sb.Append(' ').AppendJoin(' ', b.Tags);

            try
            {
                string body = await _wb.Problems.LocalStatementTextAsync(b.Id);
                if (!string.IsNullOrWhiteSpace(body))
                    sb.Append(' ').Append(body.Length > SearchSnippetChars ? body[..SearchSnippetChars] : body);
            }
            catch { /* 单题读不到不影响整份索引 */ }

            map[b.Id] = sb.ToString();
        }
        return map;
    }

    /// <summary>索引里每题正文最多取多少字符，避免把整篇题面灌进向量空间。</summary>
    private const int SearchSnippetChars = 600;

    public async Task<string> CompileStdAsync(int id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var doc = await GetProblemAsync(id, ct);
        if (doc is null) return $"题目 P{id} 不存在，无法编译。";

        var r = await _wb.Problems.CompileStdAsync(id, doc.StdCode);
        return r.Ok ? "编译成功。" + r.Message : "编译失败：" + r.Message;
    }
}
