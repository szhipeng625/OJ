using System.Text.Json;
using author.DataAccess.Interop;
using author.DataAccess.Models;

namespace author.DataAccess;

/// <summary>比赛数据访问：全部走 MySQL 云端（不再本地保存 contests 目录）。</summary>
public sealed class ContestClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public List<ContestInfo> List()
    {
        try
        {
            return JsonSerializer.Deserialize<List<ContestInfo>>(OjCoreInterop.ListContestsJson(), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>把比赛配置直接发布到 MySQL 云端（客户端从此拉取），不写本地文件。</summary>
    public string PublishDraft(ContestDraft draft)
    {
        string json = JsonSerializer.Serialize(new
        {
            id = draft.Id,
            name = draft.Name,
            description = "",
            startTime = draft.StartTime,
            endTime = draft.EndTime,
            problems = draft.ProblemIds
        });
        return OjCoreInterop.PublishContest(draft.Id, json);
    }

    /// <summary>读取一场比赛的可编辑草稿（从 MySQL），失败返回 null。</summary>
    public ContestDraft? GetDraft(int cid)
    {
        try
        {
            using var doc = JsonDocument.Parse(OjCoreInterop.GetContestJson(cid));
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string start = root.TryGetProperty("startTime", out var s) ? s.GetString() ?? "" : "";
            string end = root.TryGetProperty("endTime", out var en) ? en.GetString() ?? "" : "";
            int[] ids = root.TryGetProperty("problems", out var p)
                ? p.EnumerateArray().Select(x => x.GetInt32()).ToArray()
                : Array.Empty<int>();
            return new ContestDraft(cid, name, start, end, ids);
        }
        catch { return null; }
    }
}
