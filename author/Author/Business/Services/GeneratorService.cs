using System.Text.Json;
using author.DataAccess;
using author.DataAccess.Models;

namespace author.Business.Services;

/// <summary>
/// 数据生成器业务（全局库模型）：生成器代码/描述存 MySQL（generators 表），
/// 题目通过 problem_generators 关联；本地编译/运行经 authorcore 的临时测试工作区完成。
/// 所有文件与子进程操作经 <see cref="BuildService"/> 调度。
/// </summary>
public sealed class GeneratorService
{
    private readonly GeneratorClient _db;
    private readonly AuthorClient _author;
    private readonly BuildService _build;

    public GeneratorService(GeneratorClient db, AuthorClient author, BuildService build)
    {
        _db = db;
        _author = author;
        _build = build;
    }

    // 生成器库操作用固定 gate（MySQL 全局串行由 OjCoreInterop.Lock 保证）；
    // 本地编译/运行按生成器 id 分 gate，避免同目录 exe/数据竞争。
    private const string LibGate = "gen-lib";
    private static string Gate(int id) => "g" + id;

    private const string GenTemplate =
        "// 数据生成器 {name}.cpp\n"
        + "// 运行约定：每个测试点独立运行一次，直接把测试数据输出到 stdout（cout）。\n"
        + "// 后台会把 stdout 重定向写入 1.in ~ n.in。\n"
        + "// argv[1]=输出目录, argv[2]=随机种子, argv[3]=总组数 n, argv[4]=当前组号 i（1 起）。\n"
        + "#include <bits/stdc++.h>\n"
        + "using namespace std;\n"
        + "int main(int argc, char** argv) {\n"
        + "    int seed = (argc > 2) ? atoi(argv[2]) : 1;\n"
        + "    mt19937 rng((unsigned)seed);\n"
        + "    uniform_int_distribution<int> dist(1, 100);\n"
        + "    int m = dist(rng);\n"
        + "    cout << m << \"\\n\";\n"
        + "    for (int j = 0; j < m; ++j) cout << (j ? \" \" : \"\") << dist(rng);\n"
        + "    cout << \"\\n\";\n"
        + "    return 0;\n"
        + "}\n";

    // ===== 库（全局） =====
    public Task<List<GenLibItem>> ListAsync()
        => _build.RunAsync(LibGate, () => _db.ListGenerators());

    public Task<List<GenBoundItem>> ListUsedAsync(int problemId)
        => _build.RunAsync(LibGate, () => _db.ListProblemGenerators(problemId));

    public Task<GenDetail?> LoadAsync(int id)
        => _build.RunAsync(Gate(id), () => _db.GetGenerator(id));

    public Task<(bool Ok, string Message, int Id)> CreateAsync(string name, string desc)
        => _build.RunAsync(LibGate, () => _db.Create(name, GenTemplate.Replace("{name}", name), desc));

    public Task<JobResult> SaveAsync(int id, string code)
        => _build.RunAsync(Gate(id), () =>
        {
            var d = _db.GetGenerator(id);
            if (d == null) return new JobResult(false, "生成器不存在");
            var (ok, msg) = _db.Update(id, code, d.Desc);
            return new JobResult(ok, ok ? "已保存" : msg);
        });

    public Task<JobResult> SetDescAsync(int id, string desc)
        => _build.RunAsync(Gate(id), () =>
        {
            var d = _db.GetGenerator(id);
            if (d == null) return new JobResult(false, "生成器不存在");
            var (ok, msg) = _db.Update(id, d.Code, desc);
            return new JobResult(ok, ok ? "描述已更新" : msg);
        });

    // ===== 绑定 / 解绑 =====
    public Task<JobResult> BindAsync(int problemId, int generatorId, int genCount)
        => _build.RunAsync(LibGate, () =>
        {
            var (ok, msg) = _db.Bind(problemId, generatorId, genCount);
            return new JobResult(ok, ok ? "已绑定" : msg);
        });

    public Task<JobResult> UnbindAsync(int problemId, int generatorId)
        => _build.RunAsync(LibGate, () =>
        {
            var (ok, msg) = _db.Unbind(problemId, generatorId);
            return new JobResult(ok, ok ? "已解绑" : msg);
        });

    // ===== 本地测试（编译 / 运行 / 文件预览） =====
    public Task<JobResult> CompileAsync(int id, string name, string code)
        => _build.RunAsync(Gate(id), () =>
        {
            var d = _db.GetGenerator(id);
            if (d != null) _db.Update(id, code, d.Desc);   // 编译前先落库
            string r = _author.GenTestCompile(id, name, code);
            return r.Contains("\"ok\":true")
                ? new JobResult(true, "编译成功 ✓")
                : new JobResult(false, "编译失败：" + AuthorClient.ParseError(r));
        });

    public Task<JobResult> RunAsync(int id, string name, int n)
        => _build.RunAsync(Gate(id), () =>
        {
            var d = _db.GetGenerator(id);
            if (d == null) return new JobResult(false, "生成器不存在");
            string cr = _author.GenTestCompile(id, name, d.Code);
            if (!cr.Contains("\"ok\":true"))
                return new JobResult(false, "编译失败：" + AuthorClient.ParseError(cr));
            string r = _author.GenTestRun(id, name, n);
            try
            {
                using var doc = JsonDocument.Parse(r);
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    int generated = root.TryGetProperty("generated", out var g) ? g.GetInt32() : 0;
                    int total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                    var fails = new List<string>();
                    if (root.TryGetProperty("fails", out var fa))
                        foreach (var x in fa.EnumerateArray()) fails.Add(x.GetString() ?? "");
                    string msg = $"完成：成功 {generated}/{total} 组"
                               + (fails.Count > 0 ? "；失败：" + string.Join("；", fails.Take(5)) : "");
                    return new JobResult(true, msg);
                }
                return new JobResult(false, "生成失败：" + AuthorClient.ParseError(r));
            }
            catch { return new JobResult(false, "生成失败：" + r); }
        });

    public Task<List<GenFile>> FilesAsync(int id, string name)
        => _build.RunAsync(Gate(id), () => _author.ListGenTestFiles(id, name));

    public Task<(string Text, bool Truncated)> GetFileAsync(int id, string name, string file)
        => _build.RunAsync(Gate(id), () => _author.GetGenTestFile(id, name, file));

    public Task<List<GenSearchHit>> SearchAsync(string keyword)
        => _build.RunAsync(LibGate, () => _db.Search(keyword));
}