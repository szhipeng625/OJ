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
    public string GenDir(int id, string name) => Path.Combine(ProblemDir(id), name);
    public string GenSrcPath(int id, string name) => Path.Combine(GenDir(id, name), "gen.cpp");
    public string GenOutDir(int id, string name) => Path.GetFullPath(Path.Combine(ProblemDir(id), name));
    public string GenExePath(int id, string name) => Path.Combine(TempRoot, id.ToString(), name + ".exe");
    public string StdExePath(int id) => Path.Combine(TempRoot, id.ToString(), "std.exe");

    // ===== 题目列表 / 创建 =====
    public List<ProblemInfo> List()
    {
        try
        {
            return JsonSerializer.Deserialize<List<ProblemInfo>>(AuthorCoreInterop.List(), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    public string Create(int id, string? title)
        => AuthorCoreInterop.Create(id,
            string.IsNullOrWhiteSpace(title) ? "新题目" : title,
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
                return new ProblemMeta(false, 1000, 256, Array.Empty<string>(), "");
            var tags = r.TryGetProperty("tags", out var ta)
                ? ta.EnumerateArray().Select(t => t.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray()
                : Array.Empty<string>();
            return new ProblemMeta(true,
                r.GetProperty("timeLimitMs").GetInt32(),
                r.GetProperty("memLimitMB").GetInt32(),
                tags,
                r.TryGetProperty("updatedAt", out var u) ? u.GetString() ?? "" : "");
        }
        catch { return new ProblemMeta(false, 1000, 256, Array.Empty<string>(), ""); }
    }

    /// <summary>读取一道题的题面、样例、元数据、标程全文。</summary>
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
        return new ProblemContent(
            title, desc,
            ReadFile("sample.in"), ReadFile("sample.out"),
            GetMeta(id),
            File.Exists(stdPath) ? File.ReadAllText(stdPath) : StdTemplate);
    }

    public void WriteProblemFile(int id, string name, string content)
        => File.WriteAllText(Path.Combine(ProblemDir(id), name), content);

    // ===== 编译 / 生成答案 / 校验 / 发布（返回原生 JSON，由 BLL 解析） =====
    public string Compile(string src, string exe) => AuthorCoreInterop.Compile(src, exe);
    public string GenOutputs(int id, string stdExe) => AuthorCoreInterop.GenOutputs(id, stdExe);
    public string Validate(int id) => AuthorCoreInterop.Validate(id);
    public string Publish(int id, string targetRoot) => AuthorCoreInterop.Publish(id, targetRoot);

    /// <summary>把题目（题面 + 元数据 + 测试数据文件）发布到 MySQL，供客户端拉取。</summary>
    public string PublishToMySql(int id) => OjCoreInterop.PublishProblemDir(id, ProblemDir(id));

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

    /// <summary>
    /// 从测试数据目录自动搜索 *.in / *.out 并填充到题目目录（同名文件以先搜到者为准）。
    /// 返回 (导入的 .in 数量, 导入的 .out 数量)。
    /// </summary>
    public (int InCount, int OutCount) AutoFillData(int id, string sourceDir)
    {
        string dir = ProblemDir(id);
        Directory.CreateDirectory(dir);
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return (0, 0);

        int inCnt = 0, outCnt = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(sourceDir, "*.in", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(f);
            if (!seen.Add(fileName)) continue;
            File.Copy(f, Path.Combine(dir, fileName), true);
            inCnt++;

            string baseName = Path.GetFileNameWithoutExtension(f);
            string outSrc = Path.Combine(Path.GetDirectoryName(f)!, baseName + ".out");
            if (File.Exists(outSrc))
            {
                string outName = Path.GetFileName(outSrc);
                if (seen.Add(outName))
                {
                    File.Copy(outSrc, Path.Combine(dir, outName), true);
                    outCnt++;
                }
            }
        }
        return (inCnt, outCnt);
    }

    public void DeleteDataPair(int id, string baseName)
    {
        string dir = ProblemDir(id);
        string inF = Path.Combine(dir, baseName + ".in");
        string outF = Path.Combine(dir, baseName + ".out");
        if (File.Exists(inF)) File.Delete(inF);
        if (File.Exists(outF)) File.Delete(outF);
    }

    /// <summary>
    /// 按组别（题目根目录 + 各数据生成器子目录）列出所有输入/输出数据，
    /// 供「生成标准答案」页两列展示，并可按生成器组别一一对应。
    /// </summary>
    public List<DataRow> ListGroupedRows(int id)
    {
        var rows = new List<DataRow>();
        AddGroup(rows, "", "手工数据（题目根目录）", ProblemDir(id));
        foreach (var g in ListGenerators(id))
        {
            string title = "生成器 " + g.Name + (string.IsNullOrEmpty(g.Desc) ? "" : "（" + g.Desc + "）");
            AddGroup(rows, g.Name, title, GenOutDir(id, g.Name));
        }
        return rows;
    }

    private static void AddGroup(List<DataRow> rows, string groupKey, string groupTitle, string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.in").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string baseName = Path.GetFileNameWithoutExtension(f);
            string outFile = baseName + ".out";
            bool hasOut = File.Exists(Path.Combine(dir, outFile));
            rows.Add(new DataRow(groupKey, groupTitle, baseName, Path.GetFileName(f), outFile, hasOut));
        }
    }

    /// <summary>删除指定组别（生成器目录或题目根目录）下的一组 .in/.out。</summary>
    public void DeleteDataPairInGroup(int id, string group, string baseName)
    {
        string dir = string.IsNullOrEmpty(group) ? ProblemDir(id) : GenOutDir(id, group);
        string inF = Path.Combine(dir, baseName + ".in");
        string outF = Path.Combine(dir, baseName + ".out");
        if (File.Exists(inF)) File.Delete(inF);
        if (File.Exists(outF)) File.Delete(outF);
    }

    /// <summary>读取指定组别下某个数据文件内容（超 200KB 截断）。</summary>
    public (string Text, bool Truncated) ReadDataFile(int id, string group, string fileName)
    {
        string dir = string.IsNullOrEmpty(group) ? ProblemDir(id) : GenOutDir(id, group);
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return ("", false);
        string text = File.ReadAllText(path);
        if (text.Length > 200000) return (text[..200000], true);
        return (text, false);
    }

    // ===== 生成器本地文件（authorcore，多生成器编程） =====
    /// <summary>列出题目下所有数据生成器。</summary>
    public List<GenSummary> ListGenerators(int id)
    {
        try
        {
            return JsonSerializer.Deserialize<List<GenSummary>>(AuthorCoreInterop.GenList(id), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    public string GenCreate(int id, string name, string desc) => AuthorCoreInterop.GenCreate(id, name, desc);

    public string GenSave(int id, string name, string code) => AuthorCoreInterop.GenSave(id, name, code);

    public string GenSetDesc(int id, string name, string desc) => AuthorCoreInterop.GenSetDesc(id, name, desc);

    /// <summary>获取某生成器当前代码+描述+路径（无 gen.cpp 则返回模板）。</summary>
    public (string name, string desc, string code, string genPath, string outDir) GetGenCurrent(int id, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(AuthorCoreInterop.GenGetCurrent(id, name));
            var r = doc.RootElement;
            return (
                r.TryGetProperty("name", out var nm) ? nm.GetString() ?? name : name,
                r.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "",
                r.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "",
                r.TryGetProperty("genPath", out var g) ? g.GetString() ?? "" : "",
                r.TryGetProperty("outDir", out var o) ? o.GetString() ?? "" : "");
        }
        catch { return (name, "", "", "", ""); }
    }

    public string GenCompile(int id, string name) => AuthorCoreInterop.GenCompile(id, name);
    public string GenRun(int id, string name, int n) => AuthorCoreInterop.GenRun(id, name, n);

    public List<GenFile> ListGenFiles(int id, string name)
    {
        try
        {
            return JsonSerializer.Deserialize<List<GenFile>>(AuthorCoreInterop.GenListFiles(id, name), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

    public (string text, bool truncated) GetGenFile(int id, string name, string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(AuthorCoreInterop.GenGetFile(id, name, file));
            var r = doc.RootElement;
            string text = r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            bool truncated = r.TryGetProperty("truncated", out var tr) && tr.GetBoolean();
            return (text, truncated);
        }
        catch { return ("", false); }
    }

    public string GenImportToProblem(int id, string name, string filename) => AuthorCoreInterop.GenImportToProblem(id, name, filename);

    /// <summary>跨题在所有题目的生成器代码中查找关键字（大小写不敏感，C++ 上限 30 条）。</summary>
    public List<GenSearchResult> SearchGenerators(string keyword)
    {
        try
        {
            return JsonSerializer.Deserialize<List<GenSearchResult>>(AuthorCoreInterop.GenSearch(keyword), JsonOpts) ?? new();
        }
        catch { return new(); }
    }

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
