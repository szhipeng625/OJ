namespace author.DataAccess.Models;

/// <summary>题目列表项（对应 ac_list 返回）。</summary>
public record ProblemInfo(int Id, string Title, int DataCount, bool IsPublic = true)
{
    public string DataText => $"{DataCount} 组测试数据";
    public string VisibilityText => IsPublic ? "" : "（未公开）";
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

/// <summary>
/// 「生成标准答案」页的一行：某组别（生成器 / 根目录）下的一组输入输出数据。
/// GroupKey 用于删除时定位目录，GroupTitle 用于界面分组显示。
/// </summary>
public sealed record DataRow(string GroupKey, string GroupTitle, string BaseName, string InFile, string OutFile, bool HasOut)
{
    public string InDisplay => InFile;
    public string OutDisplay => HasOut ? OutFile : "缺 " + OutFile + "（未生成）";
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
