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
            if (r.Contains("\"ok\":true"))
            {
                string m = "";
                try { m = _client.PublishToMySql(cid); } catch { }
                string msg = $"已发布比赛 C{cid} 到 {target.Trim()}\\contests\\{cid} ✓";
                if (m.Contains("\"ok\":true")) msg += "\n已同步到 MySQL 数据库 ✓";
                else if (!string.IsNullOrEmpty(m)) msg += "\nMySQL 同步失败：" + AuthorClient.ParseError(m);
                return new JobResult(true, msg);
            }
            return new JobResult(false, "发布失败：" + AuthorClient.ParseError(r));
        });

    /// <summary>
    /// 保存比赛并发布：主渠道上传到 MySQL 数据库（客户端从此拉取），本地复制作为兜底。
    /// </summary>
    public Task<JobResult> SaveAndPublishAsync(ContestDraft draft, string serverRoot)
        => _build.RunAsync("c" + draft.Id, () =>
        {
            string r = _client.Save(draft);
            if (!r.Contains("\"ok\":true"))
                return new JobResult(false, AuthorClient.ParseError(r));

            // 1) 上传到 MySQL（主渠道，客户端从此拉取）
            string m = "";
            try { m = _client.PublishToMySql(draft.Id); } catch { }
            bool mysqlOk = m.Contains("\"ok\":true");

            // 2) 本地复制（兜底，失败不影响主流程）
            string localMsg = "";
            if (!string.IsNullOrWhiteSpace(serverRoot))
            {
                string p = _client.Publish(draft.Id, serverRoot.Trim());
                localMsg = p.Contains("\"ok\":true")
                    ? $"；已复制到 {serverRoot.Trim()}\\contests\\{draft.Id}"
                    : "；本地复制失败：" + AuthorClient.ParseError(p);
            }

            string msg = $"已保存并发布比赛 C{draft.Id}：{draft.Name}\n" +
                $"时间：{draft.StartTime} ~ {draft.EndTime}\n" +
                $"题目：{string.Join(", ", draft.ProblemIds)}\n" +
                (mysqlOk ? "已同步 MySQL ✓" : ("MySQL 同步失败：" + (string.IsNullOrEmpty(m) ? "未知错误" : AuthorClient.ParseError(m)))) +
                localMsg;
            return new JobResult(mysqlOk, msg);
        });
}
