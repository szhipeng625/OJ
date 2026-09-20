namespace client.Business.Services;

/// <summary>
/// BLL：比赛时间窗口等纯业务规则，不依赖 UI 与 DAL，可独立测试。
/// </summary>
public static class ContestPolicy
{
    /// <summary>比赛当前状态：未开始 / 进行中 / 已结束（可虚拟参赛）/ 时间未知。</summary>
    public static string Status(string? start, string? end)
    {
        var now = DateTime.Now;
        if (!DateTime.TryParse(start, out var st) || !DateTime.TryParse(end, out var et))
            return "时间未知";
        if (now < st) return "未开始";
        if (now < et) return "进行中";
        return "已结束（可虚拟参赛）";
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
}
