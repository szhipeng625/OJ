namespace client.DataAccess.Models;

/// <summary>比赛列表项</summary>
public record ContestInfo(int Id, string Name, int ProblemCount, string StartTime, string EndTime);

/// <summary>比赛详情（含题目编号列表）</summary>
public record ContestDetail(int Id, string Name, string Description, string StartTime, string EndTime, int[] Problems);

/// <summary>榜单单行</summary>
public record BoardRow(int Rank, string Username, int Solved, long Penalty);

/// <summary>榜单：正式榜 + 虚拟参赛榜</summary>
public record BoardData(List<BoardRow> Official, List<BoardRow> Virtual);

/// <summary>合并榜单一行：虚拟参赛者 Rank=0（不显示名次），但行位置按成绩排列</summary>
public record BoardEntry(int Rank, string Username, int Solved, long Penalty, bool Virtual);

/// <summary>比赛提交记录一行（列表摘要，不含代码/测试点；双击时按 Id 拉取详情）</summary>
public record ContestSubmission(long Id, int ProblemId, string Username, string Verdict,
    string Detail, int TimeMs, bool Virtual, string Ts);

/// <summary>某次比赛提交的完整详情（含代码与测试点，双击提交记录时加载）</summary>
public record SubmissionDetail(long Id, int ProblemId, int ContestId, string Username, bool Virtual,
    string Verdict, string Detail, string Ts, string Code, List<CaseResult> Cases);

/// <summary>报名状态</summary>
public record ContestRegistration(bool Ok, bool Registered, bool Virtual);
