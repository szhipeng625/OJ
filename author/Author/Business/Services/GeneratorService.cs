using author.DataAccess;
using author.DataAccess.Models;

namespace author.Business.Services;

/// <summary>
/// 数据生成器业务（多生成器模型）：生成器代码/描述均由 authorcore 在
/// 题目目录下的各生成器子目录（{题根}/{id}/{name}/）中管理，本服务负责编排、
/// 本地编译/运行（经 BuildService 调度）与数据文件查看。
/// </summary>
public sealed class GeneratorService
{
    private readonly AuthorClient _author;
    private readonly BuildService _build;

    public GeneratorService(AuthorClient author, BuildService build)
    {
        _author = author;
        _build = build;
    }

    private static string Gate(int id) => "p" + id;

    /// <summary>建生成器并返回原生结果。</summary>
    public Task<JobResult> CreateAsync(int id, string name, string desc)
        => _build.RunAsync(Gate(id), () =>
        {
            string r = _author.GenCreate(id, name, desc);
            return r.Contains("\"ok\":true")
                ? new JobResult(true, $"已创建生成器 {name}（{desc}）")
                : new JobResult(false, AuthorClient.ParseError(r));
        });

    /// <summary>列出题目下所有数据生成器。</summary>
    public Task<List<GenSummary>> ListAsync(int id)
        => _build.RunAsync(Gate(id), () => _author.ListGenerators(id));

    /// <summary>修改生成器描述。</summary>
    public Task<JobResult> SetDescAsync(int id, string name, string desc)
        => _build.RunAsync(Gate(id), () =>
        {
            string r = _author.GenSetDesc(id, name, desc);
            return r.Contains("\"ok\":true")
                ? new JobResult(true, "描述已更新")
                : new JobResult(false, AuthorClient.ParseError(r));
        });

    /// <summary>载入某生成器工作台数据（代码 + 描述 + 路径）。</summary>
    public Task<GenWorkspace> LoadAsync(int id, string name)
        => _build.RunAsync(Gate(id), () =>
        {
            var cur = _author.GetGenCurrent(id, name);
            return new GenWorkspace(cur.name, cur.desc, cur.code, cur.genPath, cur.outDir);
        });

    /// <summary>保存生成器代码（直接覆盖 gen.cpp）。</summary>
    public Task<JobResult> SaveAsync(int id, string name, string code)
        => _build.RunAsync(Gate(id), () =>
        {
            string r = _author.GenSave(id, name, code);
            return r.Contains("\"ok\":true") ? new JobResult(true, "已保存") : new JobResult(false, AuthorClient.ParseError(r));
        });

    /// <summary>保存并编译生成器。</summary>
    public Task<JobResult> CompileAsync(int id, string name, string code)
        => _build.RunAsync(Gate(id), () =>
        {
            string r = _author.GenSave(id, name, code);
            if (!r.Contains("\"ok\":true")) return new JobResult(false, "保存失败：" + AuthorClient.ParseError(r));
            string rc = _author.GenCompile(id, name);
            return rc.Contains("\"ok\":true")
                ? new JobResult(true, "编译成功 ✓（可点「生成数据」）")
                : new JobResult(false, "编译失败：" + AuthorClient.ParseError(rc));
        });

    /// <summary>运行生成器产出 n 组 .in（写入该生成器子目录，即判题数据源）。</summary>
    public Task<JobResult> RunAsync(int id, string name, int n)
        => _build.RunAsync(Gate(id), () =>
        {
            string exe = _author.GenExePath(id, name);
            if (!System.IO.File.Exists(exe))
            {
                string rc = _author.GenCompile(id, name);
                if (!rc.Contains("\"ok\":true"))
                    return new JobResult(false, "尚未编译，先编译：" + AuthorClient.ParseError(rc));
            }
            string r = _author.GenRun(id, name, n);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r);
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    int generated = root.TryGetProperty("generated", out var g) ? g.GetInt32() : 0;
                    int total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                    string outDir = root.TryGetProperty("outDir", out var od) ? od.GetString() ?? "" : "";
                    var fails = new List<string>();
                    if (root.TryGetProperty("fails", out var fa))
                        foreach (var x in fa.EnumerateArray()) fails.Add(x.GetString() ?? "");
                    string msg = $"完成：成功 {generated}/{total} 个 → {outDir}"
                               + (fails.Count > 0 ? "；失败：" + string.Join("；", fails.Take(5)) : "");
                    return new JobResult(true, msg);
                }
                return new JobResult(false, "生成失败：" + AuthorClient.ParseError(r));
            }
            catch { return new JobResult(false, "生成失败：" + r); }
        });

    /// <summary>某生成器的数据文件列表。</summary>
    public Task<List<GenFile>> FilesAsync(int id, string name)
        => _build.RunAsync(Gate(id), () => _author.ListGenFiles(id, name));

    /// <summary>读取某生成器的数据文件内容（超过 200KB 由 C++ 截断）。</summary>
    public Task<(string Text, bool Truncated)> GetFileAsync(int id, string name, string file)
        => _build.RunAsync(Gate(id), () => _author.GetGenFile(id, name, file));

    public Task<JobResult> ImportToProblemAsync(int id, string name, string filename)
        => _build.RunAsync(Gate(id), () =>
        {
            string r = _author.GenImportToProblem(id, name, filename);
            return r.Contains("\"ok\":true")
                ? new JobResult(true, $"已导入 {filename} 到题目测试数据目录")
                : new JobResult(false, "导入失败：" + AuthorClient.ParseError(r));
        });

    /// <summary>跨题查找生成器代码，并用题目列表补全标题。</summary>
    public Task<List<SearchHit>> SearchAsync(string keyword, IReadOnlyList<ProblemInfo> problems)
        => _build.RunAsync("gen-search", () =>
        {
            var hits = new List<SearchHit>();
            foreach (var it in _author.SearchGenerators(keyword))
            {
                string title = "P" + it.ProblemId;
                var p = problems.FirstOrDefault(x => x.Id == it.ProblemId);
                if (p != null) title += " " + p.Title;
                hits.Add(new SearchHit(it.ProblemId, title, it.LineNo, it.Line, it.Keyword, it.Generator));
            }
            return hits;
        });
}