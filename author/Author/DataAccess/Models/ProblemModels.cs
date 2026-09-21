namespace author.DataAccess.Models;

/// <summary>题目列表项（对应 ac_list 返回）。</summary>
public record ProblemInfo(int Id, string Title, int DataCount)
{
    public string DataText => $"{DataCount} 组测试数据";
}

/// <summary>题目元数据 meta.json。</summary>
public record ProblemMeta(bool Ok, int TimeLimitMs, int MemLimitMB, string[] Tags, string UpdatedAt);

/// <summary>测试数据配对（.in ⇄ .out）。</summary>
public record DataPair(string BaseName, bool HasOut)
{
    public string Display => HasOut
        ? $"{BaseName}.in ⇄ {BaseName}.out  ✓"
        : $"{BaseName}.in ⇄ {BaseName}.out  ✗ 缺答案";
}

/// <summary>加载一道题时需要的全部文件内容。</summary>
public record ProblemContent(
    string StatementTitle,
    string StatementDesc,
    string SampleIn,
    string SampleOut,
    ProblemMeta Meta,
    string StdCode
    )
{ }
