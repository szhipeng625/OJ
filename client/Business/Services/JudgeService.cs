using client.DataAccess;
using client.DataAccess.Models;

namespace client.Business.Services;

/// <summary>
/// BLL：题库、判题、比赛与榜单的业务入口，编排 DAL 的 ApiClient，供 UI 调用。
/// </summary>
public class JudgeService
{
    private readonly ApiClient _api;

    public JudgeService(ApiClient api) => _api = api;

    public Task<List<Problem>?> GetProblemsAsync() => _api.GetProblemsAsync();

    /// <summary>提交判题（练习/比赛统一入口，username 用于结果归属与榜单署名）。</summary>
    public Task<SubmitResult?> SubmitAsync(int problemId, string code, string username, bool virt)
        => _api.SubmitExAsync(problemId, code, username, virt);

    public Task<List<ContestInfo>?> GetContestsAsync() => _api.GetContestsAsync();

    public Task<ContestDetail?> GetContestAsync(int contestId) => _api.GetContestAsync(contestId);

    public Task<BoardData?> GetBoardAsync(int contestId) => _api.GetBoardAsync(contestId);

    /// <summary>按比赛配置的题目编号集合过滤出比赛题目（业务规则）。</summary>
    public List<Problem> FilterContestProblems(List<Problem> all, IEnumerable<int> problemIds)
    {
        var ids = new HashSet<int>(problemIds);
        return all.Where(p => ids.Contains(p.Id)).ToList();
    }
}
