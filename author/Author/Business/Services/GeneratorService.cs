using author.DataAccess;
using author.DataAccess.Models;

namespace author.Business.Services;

/// <summary>
/// 数据生成器业务：生成器代码存取（ojcore：LSM 历史 + MySQL 最新版本）、
/// 本地编译/运行（authorcore，经 BuildService 调度）、版本与数据文件查看、跨题查找。
/// </summary>
public sealed class GeneratorService
{
    private readonly GeneratorClient _gen;
    private readonly AuthorClient _author;
    private readonly BuildService _build;

    public GeneratorService(GeneratorClient gen, AuthorClient author, BuildService build)
    {
        _gen = gen;
        _author = author;
        _build = build;
    }

    private static string Gate(int id) => "p" + id;

    /// <summary>载入某题生成器工作台所需全部数据。</summary>
    public Task<GenWorkspace> LoadAsync(int id)
        => _build.RunAsync(Gate(id), () =>
        {
            string genPath = _author.GenSrcPath(id);
            string outDir = _author.GenOutDir(id);

            string code;
            var cur = _gen.GetCurrent(id);
            if (cur.Ok && !string.IsNullOrEmpty(cur.Code))
            {
                code = cur.Code;
                _author.WriteGenSource(id, code);   // 与本地编译用源文件保持同步
            }
            else
            {
                code = _author.ReadGenSource(id);
            }

            string pathText = "生成器代码：" + genPath
                + "\n数据输出固定目录：" + outDir + "（历史版本存 LSM，最新版本存 MySQL，数据文件仅本地）";

            return new GenWorkspace(code, genPath, outDir, pathText,
                _author.ListGenVersions(id), _author.ListGenFiles(id));
        });

    /// <summary>保存生成器代码（ojcore 留版本 + 同步本地源文件），返回新版本号。</summary>
    public Task<int> SaveAsync(int id, string code)
        => _build.RunAsync(Gate(id), () =>
        {
            int ver = _gen.Save(id, code);
            if (ver > 0) _author.WriteGenSource(id, code);
            return ver;
        });

    /// <summary>保存并编译生成器。</summary>
    public Task<JobResult> CompileAsync(int id, string code)
        => _build.RunAsync(Gate(id), () =>
        {
            int ver = _gen.Save(id, code);
            if (ver <= 0) return new JobResult(false, "保存失败");
            _author.WriteGenSource(id, code);
            string r = _author.GenCompile(id);
            return r.Contains("\"ok\":true")
                ? new JobResult(true, "编译成功 ✓（可点「生成数据」）")
                : new JobResult(false, "编译失败：" + AuthorClient.ParseError(r));
        });

    /// <summary>运行生成器产出 .in（authorcore 内部上限 60s）。</summary>
    public Task<JobResult> RunAsync(int id)
        => _build.RunAsync(Gate(id), () =>
        {
            string r = _author.GenRun(id);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r);
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    int total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                    string outDir = root.TryGetProperty("outDir", out var od) ? od.GetString() ?? "" : "";
                    var fails = new List<string>();
                    if (root.TryGetProperty("fails", out var fa))
                        foreach (var x in fa.EnumerateArray()) fails.Add(x.GetString() ?? "");
                    string msg = $"完成：成功 {total}/{total} 个 → {outDir}"
                               + (fails.Count > 0 ? "；失败：" + string.Join("；", fails.Take(5)) : "");
                    return new JobResult(true, msg);
                }
                return new JobResult(false, "生成失败：" + AuthorClient.ParseError(r));
            }
            catch { return new JobResult(false, "生成失败：" + r); }
        });

    public Task<List<GenVersion>> VersionsAsync(int id)
        => _build.RunAsync(Gate(id), () => _author.ListGenVersions(id));

    public Task<List<GenFile>> FilesAsync(int id)
        => _build.RunAsync(Gate(id), () => _author.ListGenFiles(id));

    /// <summary>读取指定历史版本代码；失败返回 null。</summary>
    public Task<string?> GetVersionCodeAsync(int id, int version)
        => _build.RunAsync<string?>(Gate(id), () =>
        {
            var v = _gen.GetVersion(id, version);
            return v.Ok ? v.Code : null;
        });

    /// <summary>读取生成的数据文件内容（超过 200KB 由 C++ 截断）。</summary>
    public Task<(string Text, bool Truncated)> GetFileAsync(int id, string name)
        => _build.RunAsync(Gate(id), () => _author.GetGenFile(id, name));

    public Task<JobResult> ImportToProblemAsync(int id, string name)
        => _build.RunAsync(Gate(id), () =>
        {
            string r = _author.GenImportToProblem(id, name);
            return r.Contains("\"ok\":true")
                ? new JobResult(true, $"已导入 {name} 到题目测试数据目录；可在「测试数据」页用标程生成对应 .out")
                : new JobResult(false, "导入失败：" + AuthorClient.ParseError(r));
        });

    /// <summary>跨题查找生成器代码，并用题目列表补全标题。</summary>
    public Task<List<SearchHit>> SearchAsync(string keyword, IReadOnlyList<ProblemInfo> problems)
        => _build.RunAsync("gen-search", () =>
        {
            var hits = new List<SearchHit>();
            foreach (var it in _gen.Search(keyword))
            {
                string title = "P" + it.ProblemId + " v" + it.Version;
                var p = problems.FirstOrDefault(x => x.Id == it.ProblemId);
                if (p != null) title += " " + p.Title;
                hits.Add(new SearchHit(it.ProblemId, title, 1, it.Preview, keyword));
            }
            return hits;
        });
}
