using System.IO;
using System.Text.Json;
using author.DataAccess.Interop;
using author.DataAccess.Models;

namespace author.DataAccess;

/// <summary>
/// 题目数据访问：封装 authorcore.dll 调用，以及服务端题库目录下的文件读写与 JSON 解析。
/// 所有方法都是同步的（底层为文件/子进程操作）；耗时的编译、运行、发布由 BLL BuildService
/// 放到后台线程并按题号串行、跨题并行。
/// </summary>
public sealed class AuthorClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string StdTemplate =
        "// 标准程序（用于生成答案 / 本地验证）\n#include <iostream>\nusing namespace std;\nint main() {\n    return 0;\n}\n";
    private const string SpjTemplate =
        "// 特判程序：spj.exe <用户输出> <标准输出> <输入>，退出码 0=通过\n";

    /// <summary>服务端题库根目录（author/problems）。</summary>
    public string Root { get; private set; } = "";
    /// <summary>编译产物临时目录（author/temp）。</summary>
    public string TempRoot { get; private set; } = "";

    public void Init(string root, string tempRoot)
    {
        Root = root;
        TempRoot = tempRoot;
        Directory.CreateDirectory(tempRoot);
        AuthorCoreInterop.Init(root);
        AuthorCoreInterop.GenSetTemp(tempRoot);
    }

    public string ProblemDir(int id) => Path.Combine(Root, id.ToString());
    public string GenSrcPath(int id) => Path.Combine(ProblemDir(id), id + ".cpp");
    public string GenOutDir(int id) => Path.GetFullPath(Path.Combine(Root, "..", "generated", id.ToString()));
    public string StdExePath(int id) => Path.Combine(TempRoot, id.ToString(), "std.exe");
    public string SpjExePath(int id) => Path.Combine(TempRoot, id.ToString(), "spj.exe");

    // ===== 题目列表 / 创建 =====
    public List<ProblemInfo> List()
    {
        try
        {
            return JsonSerializer.Deserialize<List<ProblemInfo>>(AuthorCoreInterop.List(), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    public string Create(int id)
        => AuthorCoreInterop.Create(id, "新题目",
            "题目描述……\n\n输入格式：\n\n输出格式：\n\n样例说明：", "", "");

    // ===== 题面 / 元数据 =====
    public string SaveStatement(int id, string? title, string? desc, string? si, string? so)
        => AuthorCoreInterop.SaveStatement(id, title, desc, si, so);

    public string SaveMeta(int id, int timeMs, int memMb, IReadOnlyList<string> tags)
    {
        string tagsJson = "[" + string.Join(",", tags.Select(t => "\"" + t.Replace("\"", "") + "\"")) + "]";
        return AuthorCoreInterop.SaveMeta(id, timeMs <= 0 ? 1000 : timeMs, memMb <= 0 ? 256 : memMb, tagsJson);
    }

    public ProblemMeta GetMeta(int id)
    {
        try
        {
            using var doc = JsonDocument.Parse(AuthorCoreInterop.GetMeta(id));
            var r = doc.RootElement;
            if (!r.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                return new ProblemMeta(false, 1000, 256, Array.Empty<string>(), 0, "");
            var tags = r.TryGetProperty("tags", out var ta)
                ? ta.EnumerateArray().Select(t => t.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray()
                : Array.Empty<string>();
            return new ProblemMeta(true,
                r.GetProperty("timeLimitMs").GetInt32(),
                r.GetProperty("memLimitMB").GetInt32(),
                tags,
                r.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                r.TryGetProperty("updatedAt", out var u) ? u.GetString() ?? "" : "");
        }
        catch { return new ProblemMeta(false, 1000, 256, Array.Empty<string>(), 0, ""); }
    }

    /// <summary>读取一道题的题面、样例、元数据、标程/spj 全文。</summary>
    public ProblemContent LoadContent(int id)
    {
        string dir = ProblemDir(id);
        string ReadFile(string name)
        {
            string p = Path.Combine(dir, name);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }

        string st = ReadFile("statement.txt");
        int nl = st.IndexOf('\n');
        string title = nl < 0 ? st : st[..nl];
        string desc = nl < 0 ? "" : st[(nl + 1)..];

        string stdPath = Path.Combine(dir, "std.cpp");
        string spjPath = Path.Combine(dir, "spj.cpp");
        return new ProblemContent(
            title, desc,
            ReadFile("sample.in"), ReadFile("sample.out"),
            GetMeta(id),
            File.Exists(stdPath) ? File.ReadAllText(stdPath) : StdTemplate,
            File.Exists(spjPath) ? File.ReadAllText(spjPath) : SpjTemplate);
    }

    public void WriteProblemFile(int id, string name, string content)
        => File.WriteAllText(Path.Combine(ProblemDir(id), name), content);

    // ===== 编译 / 生成答案 / 校验 / 发布（返回原生 JSON，由 BLL 解析） =====
    public string Compile(string src, string exe) => AuthorCoreInterop.Compile(src, exe);
    public string GenOutputs(int id, string stdExe) => AuthorCoreInterop.GenOutputs(id, stdExe);
    public string Validate(int id) => AuthorCoreInterop.Validate(id);
    public string Publish(int id, string targetRoot) => AuthorCoreInterop.Publish(id, targetRoot);

    // ===== 发布历史 =====
    public List<HistoryItem> ListHistory(int id)
    {
        try
        {
            return JsonSerializer.Deserialize<List<HistoryItem>>(AuthorCoreInterop.GetHistory(id), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>读取某次发布快照的题面/样例（供历史查看）。</summary>
    public HistoryItem ReadHistorySnapshot(int id, int version, out string title, out string desc,
        out string sampleIn, out string sampleOut)
    {
        string vdir = Path.Combine(ProblemDir(id), "history", version.ToString());
        string Read(string name)
        {
            string p = Path.Combine(vdir, name);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        string st = Read("statement.txt");
        int nl = st.IndexOf('\n');
        title = nl < 0 ? st : st[..nl];
        desc = nl < 0 ? "" : st[(nl + 1)..];
        sampleIn = Read("sample.in");
        sampleOut = Read("sample.out");
        var meta = GetMetaForDir(vdir);
        return new HistoryItem(version, title, meta.TimeLimitMs, meta.MemLimitMB, meta.Tags, meta.UpdatedAt);
    }

    private ProblemMeta GetMetaForDir(string dir)
    {
        try
        {
            string p = Path.Combine(dir, "meta.json");
            if (!File.Exists(p)) return new ProblemMeta(true, 1000, 256, Array.Empty<string>(), 0, "");
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            var tags = r.TryGetProperty("tags", out var ta)
                ? ta.EnumerateArray().Select(t => t.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray()
                : Array.Empty<string>();
            return new ProblemMeta(true,
                r.TryGetProperty("timeLimitMs", out var t) ? t.GetInt32() : 1000,
                r.TryGetProperty("memLimitMB", out var m) ? m.GetInt32() : 256,
                tags,
                r.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                r.TryGetProperty("updatedAt", out var u) ? u.GetString() ?? "" : "");
        }
        catch { return new ProblemMeta(true, 1000, 256, Array.Empty<string>(), 0, ""); }
    }

    // ===== 测试数据文件 =====
    public List<DataPair> ListDataPairs(int id)
    {
        var items = new List<DataPair>();
        string dir = ProblemDir(id);
        if (!Directory.Exists(dir)) return items;
        foreach (var f in Directory.GetFiles(dir, "*.in").OrderBy(f => f))
        {
            string baseName = Path.GetFileNameWithoutExtension(f);
            bool hasOut = File.Exists(Path.Combine(dir, baseName + ".out"));
            items.Add(new DataPair(baseName, hasOut));
        }
        return items;
    }

    public int ImportFiles(int id, IEnumerable<string> srcFiles)
    {
        string dir = ProblemDir(id);
        Directory.CreateDirectory(dir);
        int n = 0;
        foreach (var f in srcFiles)
        {
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
            n++;
        }
        return n;
    }

    public void DeleteDataPair(int id, string baseName)
    {
        string dir = ProblemDir(id);
        string inF = Path.Combine(dir, baseName + ".in");
        string outF = Path.Combine(dir, baseName + ".out");
        if (File.Exists(inF)) File.Delete(inF);
        if (File.Exists(outF)) File.Delete(outF);
    }

    // ===== 生成器本地文件（authorcore） =====
    public void WriteGenSource(int id, string code)
    {
        string p = GenSrcPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, code);
    }

    public string ReadGenSource(int id)
    {
        string p = GenSrcPath(id);
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    public List<GenVersion> ListGenVersions(int id)
    {
        var list = new List<GenVersion>();
        try
        {
            using var doc = JsonDocument.Parse(AuthorCoreInterop.GenListVersions(id));
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                list.Add(new GenVersion(
                    el.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                    el.TryGetProperty("time", out var t) ? t.GetString() ?? "" : "",
                    el.TryGetProperty("lines", out var l) ? l.GetInt32() : 0,
                    el.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : ""));
            }
        }
        catch { }
        return list;
    }

    public string GenCompile(int id) => AuthorCoreInterop.GenCompile(id);
    public string GenRun(int id) => AuthorCoreInterop.GenRun(id);

    public List<GenFile> ListGenFiles(int id)
    {
        try
        {
            return JsonSerializer.Deserialize<List<GenFile>>(AuthorCoreInterop.GenListFiles(id), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    public (string text, bool truncated) GetGenFile(int id, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(AuthorCoreInterop.GenGetFile(id, name));
            var r = doc.RootElement;
            string text = r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            bool truncated = r.TryGetProperty("truncated", out var tr) && tr.GetBoolean();
            return (text, truncated);
        }
        catch { return ("", false); }
    }

    public string GenImportToProblem(int id, string name) => AuthorCoreInterop.GenImportToProblem(id, name);

    /// <summary>从原生 JSON 中提取 error 字段，取不到时回显原文。</summary>
    public static string ParseError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.GetString() ?? json;
        }
        catch { }
        return json;
    }
}
