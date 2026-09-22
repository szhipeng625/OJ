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

    /// <summary>比赛是否已结束（用于默认勾选虚拟参赛）。</summary>
    public static bool IsEnded(string? end)
        => DateTime.TryParse(end, out var et) && DateTime.Now >= et;

    /// <summary>
    /// 正式（非虚拟）提交是否在允许的时间窗口内；不允许时 reason 给出提示。
    /// </summary>
    public static bool CanSubmitOfficial(string start, string end, bool virt, out string reason)
    {
        reason = "";
        var now = DateTime.Now;
        var st = DateTime.Parse(start);
        var et = DateTime.Parse(end);
        if (now < st && !virt)
        {
            reason = "比赛尚未开始。勾选「虚拟参赛」可提前模拟。";
            return false;
        }
        if (now >= et && !virt)
        {
            reason = "比赛已结束。勾选「虚拟参赛」可进行虚拟参与。";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 合并正式榜与虚拟榜为一张榜：按 AC 数降序、罚时升序排列（同名次正式选手在前）。
    /// 正式选手名次按其在正式选手中的先后编号；虚拟选手不编号（Rank=0），
    /// 但行位置严格按成绩插入，UI 据此把虚拟选手显示为带 *、无名次的占位行。
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
