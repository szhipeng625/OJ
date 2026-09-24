using author.DataAccess;
using author.DataAccess.Models;

namespace author.Business.Services;

/// <summary>比赛业务：列表、建议编号、详情、发布。发布到 MySQL 云端，不本地保存。</summary>
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

    /// <summary>保存并发布比赛：直接上传到 MySQL 云端（客户端从此拉取），不写本地文件。</summary>
    public Task<JobResult> SaveAndPublishAsync(ContestDraft draft, string serverRoot)
        => _build.RunAsync("c" + draft.Id, () =>
        {
            string r = _client.PublishDraft(draft);
            if (!r.Contains("\"ok\":true"))
                return new JobResult(false, AuthorClient.ParseError(r));

            return new JobResult(true,
                $"已保存并发布比赛 C{draft.Id}：{draft.Name}\n" +
                $"时间：{draft.StartTime} ~ {draft.EndTime}\n" +
                $"题目：{string.Join(", ", draft.ProblemIds)}\n" +
                "已同步到 MySQL 云端 ✓");
        });
}
