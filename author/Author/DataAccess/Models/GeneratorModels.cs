namespace author.DataAccess.Models;

// ===== ojcore 生成器存储（LSM 历史 + MySQL 最新版本） =====

public record GenCurrent(bool Ok, string Code, int Version, string UpdatedAt);
public record GenVersionItem(int Version, string Ts, int Lines, string Summary);
public record GenVersionContent(bool Ok, string Code, int Version, string Ts);
public record GenSearchItem(int ProblemId, int Version, string UpdatedAt, string Preview);

// ===== authorcore 本地生成器文件 =====

/// <summary>生成器代码的一个历史版本快照（authorcore 本地归档）。</summary>
public record GenVersion(int Version, string Ts, int Lines, string Summary)
{
    public string Time => Ts;
    public string Display => $"v{Version} · {Ts} · {Lines} 行";
}

/// <summary>生成的数据文件（本地固定目录，不入库、不发布）。</summary>
public record GenFile(string Name, long Size, string Modified)
{
    public string Display => Size >= 1024
        ? $"{Name}  ·  {Size / 1024} KB  ·  {Modified}"
        : $"{Name}  ·  {Size} B  ·  {Modified}";
}

/// <summary>跨题查找生成器代码的一条命中（供查找窗口绑定）。</summary>
public record SearchHit(int Pid, string Title, int LineNo, string Line, string Keyword);

/// <summary>某题生成器工作台的初始数据（最新代码 + 路径 + 本地版本 + 数据文件）。</summary>
public record GenWorkspace(
    string Code,
    string GenPath,
    string OutDir,
    string PathText,
    List<GenVersion> Versions,
    List<GenFile> Files);
