using System.Text.Json;
using author.DataAccess.Interop;
using author.DataAccess.Models;

namespace author.DataAccess;

/// <summary>比赛数据访问：authorcore 的 contests 目录读写与发布。</summary>
public sealed class ContestClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public List<ContestInfo> List()
    {
        try
        {
            return JsonSerializer.Deserialize<List<ContestInfo>>(AuthorCoreInterop.ContestList(), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    public string Save(ContestDraft draft)
    {
        string problemsJson = "[" + string.Join(",", draft.ProblemIds) + "]";
        return AuthorCoreInterop.ContestCreate(draft.Id, draft.Name, "", draft.StartTime, draft.EndTime, problemsJson);
    }

    public string Publish(int cid, string targetRoot) => AuthorCoreInterop.ContestPublish(cid, targetRoot);

    /// <summary>读取比赛详情，返回可直接展示的文本；失败返回 null。</summary>
    public string? GetDetailText(int cid)
    {
        try
        {
            using var doc = JsonDocument.Parse(AuthorCoreInterop.ContestGet(cid));
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string desc = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            string start = root.TryGetProperty("startTime", out var s) ? s.GetString() ?? "" : "";
            string end = root.TryGetProperty("endTime", out var en) ? en.GetString() ?? "" : "";
            string problems = root.TryGetProperty("problems", out var p) ? p.GetRawText() : "[]";
            return $"比赛 C{cid}：{name}\n时间：{start} ~ {end}\n题目 IDs：{problems}\n\n{desc}";
        }
        catch { return null; }
    }
}
