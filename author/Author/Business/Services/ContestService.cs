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
}
