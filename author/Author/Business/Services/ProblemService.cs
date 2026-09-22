using System.IO;
using System.Text.Json;
using author.DataAccess;
using author.DataAccess.Models;

namespace author.Business.Services;

/// <summary>一次编译/评测/发布任务的结果。</summary>
public record JobResult(bool Ok, string Message);

/// <summary>
/// 题目业务：题面/元数据/测试数据/标程/校验/发布的编排。
/// 所有涉及文件与子进程的操作都经 <see cref="BuildService"/> 调度：
/// 同一题目串行、不同题目并行，且退出时会被统一等待。
/// </summary>
public sealed class ProblemService
{
    private readonly AuthorClient _author;
    private readonly BuildService _build;

    public ProblemService(AuthorClient author, BuildService build)
    {
        _author = author;
        _build = build;
    }

    // 路径（供 UI 展示与“打开目录”）
    public string Root => _author.Root;
    public string ProblemDir(int id) => _author.ProblemDir(id);
    public string StdExePath(int id) => _author.StdExePath(id);
    public string GenOutDir(int id, string name) => _author.GenOutDir(id, name);

    private static string Gate(int id) => "p" + id;

    public Task<List<ProblemInfo>> ListAsync() => _build.RunAsync("list", () => _author.List());

    /// <summary>给出下一个可用的题目编号（现有最大编号 + 1，空题库时为 1）。</summary>
    public Task<int> SuggestIdAsync()
        => _build.RunAsync("list", () =>
        {
            int max = 0;
            foreach (var it in _author.List())
                if (it.Id > max) max = it.Id;
            return max + 1;
        });

    public Task<(bool Ok, string Message)> CreateAsync(int id, string title)
        => _build.RunAsync("list", () =>
        {
            string r = _author.Create(id, title);
            bool ok = r.Contains("\"ok\":true");
            return (ok, ok ? $"已创建题目 P{id}：{title}" : AuthorClient.ParseError(r));
        });

    /// <summary>从测试数据目录自动搜索 .in/.out 填充到题目，返回 (in 数量, out 数量)。</summary>
    public Task<(int InCount, int OutCount)> AutoFillDataAsync(int id, string sourceDir)
        => _build.RunAsync(Gate(id), () => _author.AutoFillData(id, sourceDir));

    public Task<ProblemContent> LoadAsync(int id)
        => _build.RunAsync(Gate(id), () => _author.LoadContent(id));

    public Task<JobResult> SaveStatementAsync(int id, string title, string desc, string sampleIn, string sampleOut,
        int timeMs, int memMb, IReadOnlyList<string> tags)
        => _build.RunAsync(Gate(id), () =>
        {
            string r = _author.SaveStatement(id, title, desc, sampleIn, sampleOut);
            if (!r.Contains("\"ok\":true"))
                return new JobResult(false, AuthorClient.ParseError(r));
            string rm = _author.SaveMeta(id, timeMs, memMb, tags);
            return rm.Contains("\"ok\":true")
                ? new JobResult(true, $"已保存（{timeMs}ms / {memMb}MB / {string.Join(",", tags)}）")
                : new JobResult(false, AuthorClient.ParseError(rm));
        });

    public Task<List<DataPair>> ListDataAsync(int id)
        => _build.RunAsync(Gate(id), () => _author.ListDataPairs(id));

    /// <summary>按组别（根目录 + 各生成器）列出输入/输出数据，供两列展示。</summary>
    public Task<List<DataRow>> ListGroupedRowsAsync(int id)
        => _build.RunAsync(Gate(id), () => _author.ListGroupedRows(id));

    /// <summary>读取指定组别下某个数据文件内容（用于对比窗口，超 200KB 截断）。</summary>
    public Task<(string Text, bool Truncated)> ReadDataFileAsync(int id, string group, string fileName)
        => _build.RunAsync(Gate(id), () => _author.ReadDataFile(id, group, fileName));

    public Task<int> ImportDataAsync(int id, IEnumerable<string> files)
        => _build.RunAsync(Gate(id), () => _author.ImportFiles(id, files));

    public Task DeleteDataAsync(int id, string group, string baseName)
        => _build.RunAsync(Gate(id), () => _author.DeleteDataPairInGroup(id, group, baseName));

    /// <summary>保存并编译标程。</summary>
    public Task<JobResult> CompileStdAsync(int id, string code)
        => CompileAsync(id, "std.cpp", _author.StdExePath(id), code,
            "编译成功 ✓（可去「测试数据」页运行生成答案）");

    private Task<JobResult> CompileAsync(int id, string srcName, string exe, string code, string okMessage)
        => _build.RunAsync(Gate(id), () =>
        {
            _author.WriteProblemFile(id, srcName, code);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            string r = _author.Compile(_author.ProblemDir(id) + "\\" + srcName, exe);
            return r.Contains("\"ok\":true")
                ? new JobResult(true, okMessage)
                : new JobResult(false, "编译失败：" + AuthorClient.ParseError(r));
        });

    /// <summary>运行标程对所有 *.in 生成 *.out（stdout 重定向到对应 .out 文件）。</summary>
    public Task<JobResult> GenOutputsAsync(int id)
        => _build.RunAsync(Gate(id), () =>
        {
            string stdExe = _author.StdExePath(id);
            if (!File.Exists(stdExe))
                return new JobResult(false, "标程尚未编译，请先保存并编译标程");
            string r = _author.GenOutputs(id, stdExe);
            try
            {
                using var doc = JsonDocument.Parse(r);
                var lines = new List<string>();
                foreach (var x in doc.RootElement.GetProperty("results").EnumerateArray())
                    lines.Add($"{x.GetProperty("file").GetString()} → {x.GetProperty("status").GetString()} ({x.GetProperty("ms").GetInt32()}ms)");
                return new JobResult(true, string.Join("\n", lines));
            }
            catch { return new JobResult(true, r); }
        });

    /// <summary>
    /// 一键生成标准答案：后台自动搜索填充 .in/.out → 编译标程 → 运行标程把输出重定向到各 .out。
    /// </summary>
    public Task<JobResult> GenerateAnswersAsync(int id, string stdCode, string testDataDir)
        => _build.RunAsync(Gate(id), () =>
        {
            string stdExe = _author.StdExePath(id);
            Directory.CreateDirectory(Path.GetDirectoryName(stdExe)!);
            _author.WriteProblemFile(id, "std.cpp", stdCode);
            string r = _author.Compile(_author.ProblemDir(id) + "\\std.cpp", stdExe);
            if (!r.Contains("\"ok\":true"))
                return new JobResult(false, "标程编译失败：" + AuthorClient.ParseError(r));

            var (inCnt, outCnt) = _author.AutoFillData(id, testDataDir);
            string fillMsg = inCnt > 0 ? $"自动搜索填充 {inCnt} 个 .in / {outCnt} 个 .out；" : "";

            string gr = _author.GenOutputs(id, stdExe);
            int okCount = 0;
            var fails = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(gr);
                foreach (var x in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    string file = x.GetProperty("file").GetString() ?? "";
                    string status = x.GetProperty("status").GetString() ?? "";
                    if (status == "OK") okCount++;
                    else fails.Add($"{file}({status})");
                }
            }
            catch { /* 解析失败按 0 处理 */ }

            if (okCount > 0)
                return new JobResult(true, fillMsg + $"标准答案生成完成：{okCount} 组 .out 已就位"
                    + (fails.Count > 0 ? "；失败：" + string.Join("、", fails.Take(5)) : ""));
            return new JobResult(false, fillMsg + "没有生成标准答案（未找到 .in 或标程运行失败）");
        });

    /// <summary>完整性校验，返回展示文本。</summary>
    public Task<string> ValidateAsync(int id)
        => _build.RunAsync(Gate(id), () =>
        {
            string r = _author.Validate(id);
            try
            {
                using var doc = JsonDocument.Parse(r);
                var root = doc.RootElement;
                var miss = root.GetProperty("missing").EnumerateArray()
                    .Select(m => m.GetString()).ToList();
                return root.GetProperty("inCount").GetInt32() + " 组数据"
                     + (root.GetProperty("hasStd").GetBoolean() ? "，有标程" : "，无标程")
                     + " (生成器 " + root.GetProperty("genCount").GetInt32() + "个)"
                     + (miss!.Count > 0 ? "；缺失：" + string.Join("、", miss) : "；完整 ✓");
            }
            catch { return r; }
        });

    /// <summary>发布题目到客户端题目目录（目录复制较慢，走后台任务），并同步到 MySQL。isPublic=false 为未公开。</summary>
    public Task<JobResult> PublishAsync(int id, string target, bool isPublic)
        => _build.RunAsync(Gate(id), () =>
        {
            if (string.IsNullOrWhiteSpace(target))
                return new JobResult(false, "请填写目标目录");
            string r = _author.Publish(id, target.Trim());
            if (r.Contains("\"ok\":true"))
            {
                string m = "";
                try { m = _author.PublishToMySql(id, isPublic); } catch { }
                string msg = $"已发布到 {target.Trim()}\\{id} ✓";
                if (m.Contains("\"ok\":true"))
                    msg += isPublic ? "\n已同步到 MySQL 数据库 ✓（公开，客户端可见）" : "\n已同步到 MySQL 数据库 ✓（未公开，仅服务端可见）";
                else if (!string.IsNullOrEmpty(m)) msg += "\nMySQL 同步失败：" + AuthorClient.ParseError(m);
                return new JobResult(true, msg);
            }
            return new JobResult(false, "发布失败：" + AuthorClient.ParseError(r));
        });

    /// <summary>全部题目的公开状态（id → 是否公开）。</summary>
    public Task<Dictionary<int, bool>> GetVisibilityAsync()
        => _build.RunAsync("list", () => _author.GetProblemVisibility());
}
