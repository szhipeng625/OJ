using client.DataAccess.Models;

namespace client.Business.Services;

/// <summary>
/// BLL：比赛时间窗口等纯业务规则，不依赖 UI 与 DAL，可独立测试。
/// </summary>
public static class ContestPolicy
{
    /// <summary>比赛当前状态：未开始 / 进行中 / 查看比赛（已结束）/ 时间未知。</summary>
    public static string Status(string? start, string? end)
    {
        var now = DateTime.Now;
        if (!DateTime.TryParse(start, out var st) || !DateTime.TryParse(end, out var et))
            return "时间未知";
        if (now < st) return "未开始";
        if (now < et) return "进行中";
        return "查看比赛";
    }

    /// <summary>距开始时间的倒计时文本；已开始/已结束返回空串。</summary>
    public static string Countdown(string? start)
    {
        if (!DateTime.TryParse(start, out var st)) return "";
        var span = st - DateTime.Now;
        if (span <= TimeSpan.Zero) return "";
        return span.TotalDays >= 1
            ? $"{span.Days} 天 {span.Hours} 小时 {span.Minutes} 分 {span.Seconds} 秒"
            : $"{span.Hours} 小时 {span.Minutes} 分 {span.Seconds} 秒";
    }

    /// <summary>距结束时间的倒计时文本（HH:MM:SS）；已结束返回空串。</summary>
    public static string Remaining(string? end)
    {
        if (!DateTime.TryParse(end, out var et)) return "";
        var span = et - DateTime.Now;
        if (span <= TimeSpan.Zero) return "";
        return span.TotalDays >= 1
            ? $"{span.Days} 天 {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>比赛是否已结束。</summary>
    public static bool IsEnded(string? end)
        => DateTime.TryParse(end, out var et) && DateTime.Now >= et;

    /// <summary>比赛是否已开始。</summary>
    public static bool IsStarted(string? start)
        => DateTime.TryParse(start, out var st) && DateTime.Now >= st;

    /// <summary>
    /// 提交是否在允许的时间窗口内（仅比赛进行期间允许提交，打星/正式一致）。
    /// </summary>
    public static bool CanSubmit(string start, string end, out string reason)
    {
        reason = "";
        var now = DateTime.Now;
        var st = DateTime.Parse(start);
        var et = DateTime.Parse(end);
        if (now < st)
        {
            reason = "比赛尚未开始，无法提交";
            return false;
        }
        if (now >= et)
        {
            reason = "比赛已结束，无法提交";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 合并正式榜与打星榜为一张榜：按 AC 数降序、罚时升序排列（同名次正式选手在前）。
    /// 正式选手名次按其在正式选手中的先后编号；打星选手不编号（Rank=0），
    /// 但行位置严格按成绩插入，UI 据此把打星选手显示为带 *、无名次的占位行。
    /// </summary>
    public static List<BoardEntry> MergeBoard(BoardData? board)
    {
        var raw = new List<(int Solved, long Penalty, bool Virt, string Name)>();
        if (board is not null)
        {
            foreach (var r in board.Official)
                raw.Add((r.Solved, r.Penalty, false, r.Username));
            foreach (var r in board.Virtual)
                raw.Add((r.Solved, r.Penalty, true, r.Username));
        }
        raw.Sort((a, b) =>
        {
            int c = b.Solved.CompareTo(a.Solved);
            if (c != 0) return c;
            c = a.Penalty.CompareTo(b.Penalty);
            if (c != 0) return c;
            c = a.Virt.CompareTo(b.Virt);   // 同分正式选手在前
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });

        var result = new List<BoardEntry>();
        int officialRank = 0;
        foreach (var r in raw)
        {
            if (!r.Virt) officialRank++;
            result.Add(new BoardEntry(r.Virt ? 0 : officialRank,
                r.Virt ? (r.Name + " *") : r.Name, r.Solved, r.Penalty, r.Virt));
        }
        return result;
    }
}
