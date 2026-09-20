namespace client.DataAccess.Models;

/// <summary>比赛列表项</summary>
public record ContestInfo(int Id, string Name, int ProblemCount, string StartTime, string EndTime);

/// <summary>比赛详情（含题目编号列表）</summary>
public record ContestDetail(int Id, string Name, string Description, string StartTime, string EndTime, int[] Problems);

/// <summary>榜单单行</summary>
public record BoardRow(int Rank, string Username, int Solved, long Penalty);

/// <summary>榜单：正式榜 + 虚拟参赛榜</summary>
public record BoardData(List<BoardRow> Official, List<BoardRow> Virtual);
