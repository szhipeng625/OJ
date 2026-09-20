using System.Text.Json;
using author.DataAccess.Interop;
using author.DataAccess.Models;

namespace author.DataAccess;

/// <summary>
/// 生成器代码存储数据访问：LSM 存历史版本、MySQL 存最新版本（重复保存原地替换）。
/// 全部经 ojcore（内部调用已由 <see cref="OjCoreInterop"/> 串行化）。
/// </summary>
public sealed class GeneratorClient
{
    /// <summary>保存生成器代码：LSM 历史版本 + MySQL upsert 最新版本。返回新版本号（0=失败）。</summary>
    public int Save(int problemId, string code)
    {
        try
        {
            using var j = JsonDocument.Parse(OjCoreInterop.GenSave(problemId, code ?? ""));
            return j.RootElement.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
        }
        catch { return 0; }
    }

    /// <summary>读取某题最新生成器代码（优先 MySQL，回退 LSM）。</summary>
    public GenCurrent GetCurrent(int problemId)
    {
        try
        {
            using var j = JsonDocument.Parse(OjCoreInterop.GenGetCurrent(problemId));
            var root = j.RootElement;
            bool ok = root.GetProperty("ok").GetBoolean();
            return new GenCurrent(
                ok,
                ok && root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                ok && root.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                ok && root.TryGetProperty("updatedAt", out var u) ? u.GetString() ?? "" : "");
        }
        catch { return new GenCurrent(false, "", 0, ""); }
    }

    /// <summary>列出某题全部历史版本（LSM），版本号降序。</summary>
    public List<GenVersionItem> ListVersions(int problemId)
    {
        var list = new List<GenVersionItem>();
        try
        {
            using var j = JsonDocument.Parse(OjCoreInterop.GenListVersions(problemId));
            foreach (var el in j.RootElement.EnumerateArray())
            {
                list.Add(new GenVersionItem(
                    el.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                    el.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "",
                    el.TryGetProperty("lines", out var l) ? l.GetInt32() : 0,
                    el.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : ""));
            }
        }
        catch { }
        return list;
    }

    /// <summary>读取指定历史版本代码。</summary>
    public GenVersionContent GetVersion(int problemId, int version)
    {
        try
        {
            using var j = JsonDocument.Parse(OjCoreInterop.GenGetVersion(problemId, version));
            var root = j.RootElement;
            bool ok = root.GetProperty("ok").GetBoolean();
            return new GenVersionContent(
                ok,
                ok && root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                ok && root.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                ok && root.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "");
        }
        catch { return new GenVersionContent(false, "", 0, ""); }
    }

    /// <summary>跨题查找生成器代码（遍历 MySQL 全部题目最新代码，大小写不敏感）。</summary>
    public List<GenSearchItem> Search(string keyword)
    {
        var list = new List<GenSearchItem>();
        try
        {
            using var j = JsonDocument.Parse(OjCoreInterop.GenSearch(keyword ?? ""));
            foreach (var el in j.RootElement.EnumerateArray())
            {
                list.Add(new GenSearchItem(
                    el.TryGetProperty("problemId", out var p) ? p.GetInt32() : 0,
                    el.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                    el.TryGetProperty("updatedAt", out var u) ? u.GetString() ?? "" : "",
                    el.TryGetProperty("preview", out var pr) ? pr.GetString() ?? "" : ""));
            }
        }
        catch { }
        return list;
    }
}
