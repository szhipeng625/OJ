using System.Text.Json.Serialization;

namespace author.DataAccess.Models;

// ===== 数据生成器库（全局 generators 表，经 ojcore MySQL） =====

/// <summary>生成器库条目（全局列表，来自 oj_mysql_list_generators）。</summary>
public record GenLibItem(int Id, string Name, string Desc)
{
    public string Display => string.IsNullOrEmpty(Desc) ? Name : $"{Name}（{Desc}）";
}

/// <summary>某题已绑定的生成器（含组数，来自 oj_mysql_problem_generators）。</summary>
public record GenBoundItem(int Id, string Name, int GenCount)
{
    public string Display => string.IsNullOrEmpty(Name) ? "" : $"{Name}（{GenCount} 组）";
}

/// <summary>单个生成器的完整内容（代码 + 描述）。</summary>
public record GenDetail(int Id, string Name, string Desc, string Code);

/// <summary>生成器本地测试产出的数据文件。</summary>
public record GenFile(string Name, long Size, string Modified)
{
    public string Display => Size >= 1024
        ? $"{Name}  ·  {Size / 1024} KB  ·  {Modified}"
        : $"{Name}  ·  {Size} B  ·  {Modified}";
}

/// <summary>生成器库勾选条目（左侧可勾选 = 本题使用）。</summary>
public sealed class GenCheckItem : System.ComponentModel.INotifyPropertyChanged
{
    public int Id { get; }
    public string Name { get; }
    public string Desc { get; }

    public string Display => string.IsNullOrEmpty(Desc) ? Name : $"{Name}（{Desc}）";

    private bool _isUsed;
    public bool IsUsed
    {
        get => _isUsed;
        set
        {
            if (_isUsed != value)
            {
                _isUsed = value;
                PropertyChanged?.Invoke(this, new(nameof(IsUsed)));
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public GenCheckItem(int id, string name, string desc, bool isUsed)
    {
        Id = id;
        Name = name;
        Desc = desc;
        _isUsed = isUsed;
    }
}

/// <summary>跨生成器搜索命中（来自 oj_mysql_search_generators）。</summary>
public sealed record GenSearchHit(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("lineNo")] int LineNo,
    [property: JsonPropertyName("line")] string Line,
    [property: JsonPropertyName("keyword")] string Keyword);

/// <summary>某题已上传的测试样例（来自 oj_mysql_list_testcases）。</summary>
public sealed record GenTestcase(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isSample")] bool IsSample);

// ===== 以下为 authorcore 本地多生成器模型的旧类型（保留兼容，新流程不再使用） =====

/// <summary>题目下一个数据生成器的摘要（ac_gen_list 返回）。</summary>
public record GenSummary(string Name, string Desc, int FileCount)
{
    public string Display => string.IsNullOrEmpty(Desc) ? Name : $"{Name}（{Desc}）";
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

/// <summary>跨题查找结果窗口展示用的一条命中（旧 UI 绑定 Pid/Title/LineNo/Line）。</summary>
public sealed record SearchHit(
    int Pid,
    string Title,
    int LineNo,
    string Line,
    string Keyword,
    string Generator);