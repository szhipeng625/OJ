namespace OjCodex;

/// <summary>题库列表项（轻量索引）。</summary>
public sealed record ProblemBrief(int Id, string Title, bool IsPublic, string[] Tags, int DataCount);

/// <summary>
/// 向量检索的一条文档：轻量索引 + 用于算相似度的文本（题目编号 + 标题 + 标签 + 本地题面片段）。
/// </summary>
public sealed record ProblemSearchDoc(ProblemBrief Brief, string Text);

/// <summary>一道题的完整内容（题面 + 元数据 + 标程 + 数据清单）。</summary>
public sealed record ProblemDoc(
    int Id,
    string Title,
    string Description,
    string SampleIn,
    string SampleOut,
    int TimeMs,
    int MemMb,
    string[] Tags,
    string StdCode,
    string[] DataFiles,
    int JudgeMode);

/// <summary>
/// Codex 触达题库的唯一入口。出题端用 Workbench 实现它，MCP 服务与面板共用同一实现，
/// 因此「面板里能干的活」和「MCP 里能干的活」天然一致，不会两套逻辑各写一遍。
/// </summary>
public interface IOjToolHost
{
    /// <summary>题库列表（含未公开；分页交给调用方，这里给全量轻量索引）。</summary>
    Task<IReadOnlyList<ProblemBrief>> ListProblemsAsync(CancellationToken ct = default);

    /// <summary>取一道题的完整内容；不存在返回 null。</summary>
    Task<ProblemDoc?> GetProblemAsync(int id, CancellationToken ct = default);

    /// <summary>写回题面与元数据。返回人类可读的结果说明。</summary>
    Task<string> SaveStatementAsync(int id, string title, string description,
        string sampleIn, string sampleOut, int timeMs, int memMb, string[] tags,
        CancellationToken ct = default);

    /// <summary>题库里已用过的全部标签（让新标签与既有风格一致）。</summary>
    Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken ct = default);

    /// <summary>读取标程源码。</summary>
    Task<string> GetStdAsync(int id, CancellationToken ct = default);

    /// <summary>编译标程，返回编译输出（成功/失败信息）。</summary>
    Task<string> CompileStdAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// 供向量检索用的可搜索文本：题目编号 + 标题 + 标签 +（有本地工作副本时）题面片段。
    ///
    /// 实现方必须只用**本地**数据 —— 这个接口在每次指令前都会被调用，
    /// 若逐题去中间件拉取，检索省下的 token 远抵不过多出来的网络往返。
    /// </summary>
    Task<IReadOnlyDictionary<int, string>> GetSearchDocumentsAsync(CancellationToken ct = default);
}
