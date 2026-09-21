namespace client.DataAccess.Models;

/// <summary>某用户在某题（某比赛，contestId=0 为练习）下的最近一次提交（含代码，用于恢复编辑器）。</summary>
public record UserSolution(bool Found, long Id, string Verdict, string Detail, string Ts, string Code);

/// <summary>某用户某题（练习或某场比赛）最近一次提交结果，用于通过/未通过标记。</summary>
public record UserProgress(int ProblemId, string Verdict, bool Ac);
