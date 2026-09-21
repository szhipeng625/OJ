using System.Text.Json.Serialization;

namespace author.DataAccess.Models;

// ===== authorcore 本地多生成器 =====

/// <summary>题目下一个数据生成器的摘要（ac_gen_list 返回）。</summary>
public record GenSummary(string Name, string Desc, int FileCount)
{
    public string Display => string.IsNullOrEmpty(Desc) ? Name : $"{Name}（{Desc}）";
}

/// <summary>某生成器目录下的数据文件（判题数据源）。</summary>
public record GenFile(string Name, long Size, string Modified)
{
    public string Display => Size >= 1024
        ? $"{Name}  ·  {Size / 1024} KB  ·  {Modified}"
        : $"{Name}  ·  {Size} B  ·  {Modified}";
}

/// <summary>某生成器工作台的初始数据（代码 + 描述 + 路径）。</summary>
public record GenWorkspace(
    string Name,
    string Desc,
    string Code,
    string GenPath,
    string OutDir);

/// <summary>ac_gen_search 返回的一条原生命中（跨题查找生成器代码，C++ 字段 pid/gen）。</summary>
public sealed record GenSearchResult(
    [property: JsonPropertyName("pid")] int ProblemId,
    [property: JsonPropertyName("lineNo")] int LineNo,
    [property: JsonPropertyName("line")] string Line,
    [property: JsonPropertyName("keyword")] string Keyword,
    [property: JsonPropertyName("gen")] string Generator);

/// <summary>跨题查找结果窗口展示用的一条命中（UI 绑定 Pid/Title/LineNo/Line）。</summary>
public sealed record SearchHit(
    int Pid,
    string Title,
    int LineNo,
    string Line,
    string Keyword,
    string Generator);