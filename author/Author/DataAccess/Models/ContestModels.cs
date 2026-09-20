namespace author.DataAccess.Models;

/// <summary>比赛列表项。</summary>
public record ContestInfo(int Id, string Name, int ProblemCount, string StartTime, string EndTime)
{
    public string Display => $"[{Id}] {Name}（{ProblemCount} 题 · {StartTime} ~ {EndTime}）";
}

/// <summary>比赛保存请求。</summary>
public record ContestDraft(int Id, string Name, string StartTime, string EndTime, int[] ProblemIds);
