using System.Text.Json;
using author.DataAccess.Interop;
using author.DataAccess.Models;

namespace author.DataAccess;

/// <summary>
/// 数据生成器库数据访问：全局 generators 表 + problem_generators 关联（经 ojcore MySQL）。
/// 所有方法同步（底层 ojcore 调用在 OjCoreInterop.Lock 内串行）。
/// </summary>
public sealed class GeneratorClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public List<GenLibItem> ListGenerators()
    {
        try { return JsonSerializer.Deserialize<List<GenLibItem>>(OjCoreInterop.ListGeneratorsJson(), JsonOpts) ?? new(); }
        catch { return new(); }
    }

    public List<GenBoundItem> ListProblemGenerators(int problemId)
    {
        try { return JsonSerializer.Deserialize<List<GenBoundItem>>(OjCoreInterop.ProblemGeneratorsJson(problemId), JsonOpts) ?? new(); }
        catch { return new(); }
    }

    public GenDetail? GetGenerator(int id)
    {
        try
        {
            using var doc = JsonDocument.Parse(OjCoreInterop.GetGeneratorJson(id));
            var r = doc.RootElement;
            if (!r.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
            return new GenDetail(
                r.GetProperty("id").GetInt32(),
                r.GetProperty("name").GetString() ?? "",
                r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                r.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "");
        }
        catch { return null; }
    }

    public (bool Ok, string Message, int Id) Create(string name, string code, string desc)
    {
        string r = OjCoreInterop.CreateGeneratorJson(name, code, desc);
        try
        {
            using var doc = JsonDocument.Parse(r);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                return (true, "已创建", root.GetProperty("id").GetInt32());
            return (false, ParseError(r), 0);
        }
        catch { return (false, r, 0); }
    }

    public (bool Ok, string Message) Update(int id, string code, string desc)
    {
        string r = OjCoreInterop.UpdateGeneratorJson(id, code, desc);
        if (r.Contains("\"ok\":true")) return (true, "已保存");
        return (false, ParseError(r));
    }
    public (bool Ok, string Message) Delete(int id)
    {
        string r = OjCoreInterop.DeleteGeneratorJson(id);
        if (r.Contains("\"ok\":true")) return (true, "已删除生成器");
        return (false, ParseError(r));
    }
    public (bool Ok, string Message) Bind(int problemId, int generatorId, int genCount)
    {
        string r = OjCoreInterop.BindGeneratorJson(problemId, generatorId, genCount);
        if (r.Contains("\"ok\":true")) return (true, "已绑定");
        return (false, ParseError(r));
    }

    public (bool Ok, string Message) Unbind(int problemId, int generatorId)
    {
        string r = OjCoreInterop.UnbindGeneratorJson(problemId, generatorId);
        if (r.Contains("\"ok\":true")) return (true, "已解绑");
        return (false, ParseError(r));
    }

    public List<GenSearchHit> Search(string keyword)
    {
        try { return JsonSerializer.Deserialize<List<GenSearchHit>>(OjCoreInterop.SearchGeneratorsJson(keyword), JsonOpts) ?? new(); }
        catch { return new(); }
    }

    // ===== 题目测试样例 =====
    public List<GenTestcase> ListTestcases(int problemId)
    {
        try { return JsonSerializer.Deserialize<List<GenTestcase>>(OjCoreInterop.ListTestcasesJson(problemId), JsonOpts) ?? new(); }
        catch { return new(); }
    }

    public (bool Ok, string Message) SetTestcase(int problemId, string name, string input, string output, bool isSample)
    {
        string r = OjCoreInterop.SetTestcaseJson(problemId, name, input, output, isSample);
        if (r.Contains("\"ok\":true")) return (true, "已上传测试样例");
        return (false, ParseError(r));
    }

    public (bool Ok, string Message) RemoveTestcase(int problemId, string name)
    {
        string r = OjCoreInterop.RemoveTestcaseJson(problemId, name);
        if (r.Contains("\"ok\":true")) return (true, "已移除测试样例");
        return (false, ParseError(r));
    }

    private static string ParseError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? json;
        }
        catch { }
        return json;
    }
}