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

    /// <summary>获取某用户在某题（某比赛，contestId=0 为练习）下的最近一次提交（含代码）。</summary>
    public Task<UserSolution?> GetUserSolutionAsync(int problemId, string username, int contestId)
        => _api.GetUserSolutionAsync(problemId, username, contestId);

    /// <summary>获取某用户练习模式每题最近一次提交结果（用于题库通过/未通过标记）。</summary>
    public Task<List<UserProgress>?> GetUserProgressAsync(string username)
        => _api.GetUserProgressAsync(username);

    /// <summary>获取某用户在某场比赛下每题最近一次提交结果。</summary>
    public Task<List<UserProgress>?> GetUserContestProgressAsync(int contestId, string username)
        => _api.GetUserContestProgressAsync(contestId, username);

    /// <summary>提交判题（练习/比赛统一入口，username 用于结果归属与榜单署名）。</summary>
    public Task<SubmitResult?> SubmitAsync(int problemId, string code, string username, bool virt)
        => _api.SubmitExAsync(problemId, code, username, virt);

    public Task<List<ContestInfo>?> GetContestsAsync() => _api.GetContestsAsync();

    public Task<ContestDetail?> GetContestAsync(int contestId) => _api.GetContestAsync(contestId);

    public Task<BoardData?> GetBoardAsync(int contestId) => _api.GetBoardAsync(contestId);

    public Task<SubmitResult?> SubmitContestAsync(int problemId, string code, string username, bool virt, int contestId)
        => _api.SubmitContestAsync(problemId, code, username, virt, contestId);

    public Task<ContestRegistration> RegisterContestAsync(int contestId, string username, bool virt)
        => _api.RegisterContestAsync(contestId, username, virt);

    public Task<ContestRegistration> GetContestRegistrationAsync(int contestId, string username)
        => _api.GetContestRegistrationAsync(contestId, username);

    public Task<List<ContestSubmission>?> GetContestSubmissionsAsync(int contestId, string username, bool viewAll)
        => _api.GetContestSubmissionsAsync(contestId, username, viewAll);

    /// <summary>按比赛配置的题目编号集合过滤出比赛题目（业务规则）。</summary>
    public List<Problem> FilterContestProblems(List<Problem> all, IEnumerable<int> problemIds)
    {
        var ids = new HashSet<int>(problemIds);
        return all.Where(p => ids.Contains(p.Id)).ToList();
    }
}
