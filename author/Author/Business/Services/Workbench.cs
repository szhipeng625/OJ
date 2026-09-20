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
}
