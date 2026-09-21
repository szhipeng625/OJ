using author.DataAccess;
using author.DataAccess.Models;

namespace author.Business.Services;

/// <summary>比赛业务：保存、发布、详情。发布涉及目录复制，走后台任务并按比赛编号串行。</summary>
public sealed class ContestService
{
    private readonly ContestClient _client;
    private readonly BuildService _build;

    public ContestService(ContestClient client, BuildService build)
    {
        _client = client;
        _build = build;
    }

    public Task<List<ContestInfo>> ListAsync()
        => _build.RunAsync("contest-list", () => _client.List());

    /// <summary>下一个可用的比赛编号（现有最大编号 + 1，无比赛时为 1）。</summary>
    public Task<int> SuggestIdAsync()
        => _build.RunAsync("contest-list", () =>
        {
            int max = 0;
            foreach (var it in _client.List())
                if (it.Id > max) max = it.Id;
            return max + 1;
        });

    public Task<ContestDraft?> GetDraftAsync(int cid)
        => _build.RunAsync("c" + cid, () => _client.GetDraft(cid));

    public Task<string?> GetDetailAsync(int cid)
        => _build.RunAsync("c" + cid, () => _client.GetDetailText(cid));

    public Task<JobResult> SaveAsync(ContestDraft draft)
        => _build.RunAsync("c" + draft.Id, () =>
        {
            string r = _client.Save(draft);
            return r.Contains("\"ok\":true")
                ? new JobResult(true,
                    $"已保存比赛 C{draft.Id}：{draft.Name}\n时间：{draft.StartTime} ~ {draft.EndTime}\n题目：{string.Join(", ", draft.ProblemIds)}")
                : new JobResult(false, AuthorClient.ParseError(r));
        });

    public Task<JobResult> PublishAsync(int cid, string target)
        => _build.RunAsync("c" + cid, () =>
        {
            if (string.IsNullOrWhiteSpace(target))
                return new JobResult(false, "请填写目标目录");
            string r = _client.Publish(cid, target.Trim());
            return r.Contains("\"ok\":true")
                ? new JobResult(true, $"已发布比赛 C{cid} 到 {target.Trim()}\\contests\\{cid} ✓")
                : new JobResult(false, "发布失败：" + AuthorClient.ParseError(r));
        });

    /// <summary>
    /// 保存比赛并自动发布到客户端的 contest 目录（serverRoot\contests\{cid}），一步完成。
    /// </summary>
    public Task<JobResult> SaveAndPublishAsync(ContestDraft draft, string serverRoot)
        => _build.RunAsync("c" + draft.Id, () =>
        {
            string r = _client.Save(draft);
            if (!r.Contains("\"ok\":true"))
                return new JobResult(false, AuthorClient.ParseError(r));

            if (string.IsNullOrWhiteSpace(serverRoot))
                return new JobResult(true,
                    $"已保存比赛 C{draft.Id}：{draft.Name}\n题目：{string.Join(", ", draft.ProblemIds)}\n（未配置客户端目录，仅本地保存）");

            string p = _client.Publish(draft.Id, serverRoot.Trim());
            if (!p.Contains("\"ok\":true"))
                return new JobResult(false, "已保存，但发布失败：" + AuthorClient.ParseError(p));

            return new JobResult(true,
                $"已保存并发布比赛 C{draft.Id}：{draft.Name}\n" +
                $"时间：{draft.StartTime} ~ {draft.EndTime}\n" +
                $"题目：{string.Join(", ", draft.ProblemIds)}\n" +
                $"客户端目录：{serverRoot.Trim()}\\contests\\{draft.Id} ✓");
        });
}
