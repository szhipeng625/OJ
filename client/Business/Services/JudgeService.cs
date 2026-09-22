using System.Text;
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

    /// <summary>本地调试：编译并运行用户代码（不提交、不判题）。</summary>
    public Task<DebugRunResult?> DebugRunAsync(string code, string input, int timeoutMs = 2000)
        => _api.DebugRunAsync(code, input, timeoutMs);

    /// <summary>本地调试（样例比对）：用 sample.in 作输入运行，与 sample.out 比对。</summary>
    public Task<DebugTestResult?> DebugTestAsync(int problemId, string code, int timeoutMs = 2000)
        => _api.DebugTestAsync(problemId, code, timeoutMs);

    /// <summary>把样例输入/输出写到本地题目根目录 sample.in / sample.out。</summary>
    public void WriteSampleFiles(int problemId, string input, string output)
        => _api.WriteSampleFiles(problemId, input, output);

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

    /// <summary>获取某次比赛提交的完整详情（含代码与测试点）。</summary>
    public Task<SubmissionDetail?> GetSubmissionDetailAsync(long sid)
        => _api.GetSubmissionDetailAsync(sid);

    /// <summary>按比赛配置的题目编号集合顺序返回比赛题目（业务规则）。</summary>
    public List<Problem> FilterContestProblems(List<Problem> all, IEnumerable<int> problemIds)
    {
        var map = all.ToDictionary(p => p.Id);
        return problemIds
            .Select(id => map.TryGetValue(id, out var p) ? p : null)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();
    }

    /// <summary>题目标签：0→A，1→B，…，25→Z，26→AA …（双射 26 进制）。</summary>
    public static string ProblemLabel(int index)
    {
        var sb = new StringBuilder();
        int n = index + 1;
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }
}
