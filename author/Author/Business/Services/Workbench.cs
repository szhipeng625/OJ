namespace author.Business.Services;

/// <summary>
/// 工作台服务集合（组合根装配后整体传给各窗口），避免构造函数参数过多。
/// </summary>
public sealed class Workbench
{
    public required AuthService Auth { get; init; }
    public required ProblemService Problems { get; init; }
    public required GeneratorService Generators { get; init; }
    public required ContestService Contests { get; init; }
    public required BuildService Build { get; init; }

    /// <summary>客户端服务端根目录（serverRoot，其下为 problems/contests），比赛自动发布到此。</summary>
    public string ServerRoot { get; init; } = "";

    /// <summary>题目发布目标目录（serverRoot\problems），固定不可修改。</summary>
    public string ServerProblemDir { get; init; } = "";
}
