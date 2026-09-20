using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using author.Services;

namespace author;

public record ProblemInfo(int Id, string Title, int DataCount)
{
    public string DataText => $"{DataCount} 组测试数据";
}

public record HistoryItem(int Version, string Title, int TimeLimitMs, int MemLimitMB, string[] Tags, string UpdatedAt)
{
    public string Display => $"v{Version} · {UpdatedAt} · {Title}";
}

public record ContestInfo(int Id, string Name, int ProblemCount, string StartTime, string EndTime)
{
    public string Display => $"[{Id}] {Name}（{ProblemCount} 题 · {StartTime} ~ {EndTime}）";
}

/// <summary>生成器代码的一个历史版本快照。</summary>
public record GenVersion(int Version, string Ts, int Lines, string Summary)
{
    public string Time => Ts;
    public string Display => $"v{Version} · {Ts} · {Lines} 行";
}

/// <summary>生成的数据文件（本地固定目录，不入库、不发布）。</summary>
public record GenFile(string Name, long Size, string Modified)
{
    public string Display => Size >= 1024
        ? $"{Name}  ·  {Size / 1024} KB  ·  {Modified}"
        : $"{Name}  ·  {Size} B  ·  {Modified}";
}

public partial class MainWindow : Window
{
    // 服务端题库目录（独立于客户端题目目录）
    private readonly string _root = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "author", "problems"));
    private readonly string _tempRoot;
    private int? _currentId;
    private List<ProblemInfo> _problems = new();

    public string RootText => "题库目录：" + _root;

    public MainWindow()
    {
        InitializeComponent();
        _tempRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "author", "temp");
        Directory.CreateDirectory(_tempRoot);
        try
        {
            AuthorInterop.Init(_root);
            AuthorInterop.GenSetTemp(_tempRoot);
            RefreshList();
            RefreshContests();
        }
        catch (Exception ex)
        {
            Status("初始化失败：" + ex.Message);
        }
    }

    private void Status(string msg) => StatusText.Text = msg;

    private void RefreshList()
    {
        _problems = JsonSerializer.Deserialize<List<ProblemInfo>>(
            AuthorInterop.List(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        ProblemList.ItemsSource = _problems;
        Status($"共 {_problems.Count} 道题");
    }

    private string ProblemDir(int id) => Path.Combine(_root, id.ToString());

    // 当前题目的数据输出目录（由 C++ 后端返回，供"打开输出目录"使用）
    private string _genOutDir = "";

    // ---------- 左侧 ----------
    private void OnNewProblem(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NewIdBox.Text.Trim(), out int id) || id <= 0)
        {
            Status("请输入合法的题目编号（正整数）");
            return;
        }
        string r = AuthorInterop.Create(id, "新题目", "题目描述……\n\n输入格式：\n\n输出格式：\n\n样例说明：", "", "");
        if (r.Contains("\"ok\":true"))
        {
            RefreshList();
            SelectProblem(id);
            Status($"已创建题目 P{id}");
        }
        else
        {
            Status(ParseError(r));
        }
    }

    private void SelectProblem(int id)
    {
        var item = _problems.FirstOrDefault(p => p.Id == id);
        if (item == null) return;
        ProblemList.SelectedItem = item;
        ProblemList.ScrollIntoView(item);
    }

    private void OnSelectProblem(object sender, SelectionChangedEventArgs e)
    {
        if (ProblemList.SelectedItem is not ProblemInfo p) return;
        LoadProblem(p.Id);
    }

    private void LoadProblem(int id)
    {
        _currentId = id;
        Tabs.IsEnabled = true;
        string dir = ProblemDir(id);

        // 题面：statement.txt 第一行为标题，其余为描述
        string st = File.Exists(Path.Combine(dir, "statement.txt"))
            ? File.ReadAllText(Path.Combine(dir, "statement.txt")) : "";
        int nl = st.IndexOf('\n');
        TitleBox.Text = nl < 0 ? st : st[..nl];
        DescBox.Text = nl < 0 ? "" : st[(nl + 1)..];
        SampleInBox.Text = File.Exists(Path.Combine(dir, "sample.in")) ? File.ReadAllText(Path.Combine(dir, "sample.in")) : "";
        SampleOutBox.Text = File.Exists(Path.Combine(dir, "sample.out")) ? File.ReadAllText(Path.Combine(dir, "sample.out")) : "";
        StatementMsg.Text = "";
        StdMsg.Text = ""; SpjMsg.Text = ""; DataMsg.Text = ""; PublishMsg.Text = "";

        // 元数据：时间/内存限制、标签（ac_get_meta）
        try
        {
            using var meta = JsonDocument.Parse(AuthorInterop.GetMeta(id));
            if (meta.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                TimeLimitBox.Text = meta.RootElement.GetProperty("timeLimitMs").GetInt32().ToString();
                MemLimitBox.Text = meta.RootElement.GetProperty("memLimitMB").GetInt32().ToString();
                var tags = meta.RootElement.GetProperty("tags").EnumerateArray()
                    .Select(t => t.GetString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                TagsBox.Text = string.Join(",", tags);
            }
        }
        catch { TimeLimitBox.Text = "1000"; MemLimitBox.Text = "256"; TagsBox.Text = ""; }

        StdBox.Text = File.Exists(Path.Combine(dir, "std.cpp")) ? File.ReadAllText(Path.Combine(dir, "std.cpp")) : "// 标准程序（用于生成答案 / 本地验证）\n#include <iostream>\nusing namespace std;\nint main() {\n    return 0;\n}\n";
        SpjBox.Text = File.Exists(Path.Combine(dir, "spj.cpp")) ? File.ReadAllText(Path.Combine(dir, "spj.cpp")) : "// 特判程序：spj.exe <用户输出> <标准输出> <输入>，退出码 0=通过\n";

        RefreshDataList();
        RefreshHistory();
        LoadGen(id);
    }

    // ---------- 数据生成 ----------
    // ---------- 数据生成（存储全部走 authorcore C++ 后端，前端只展示） ----------
    private void LoadGen(int id)
    {
        GenMsg.Text = "";
        GenVersionView.Text = "";
        GenFileView.Text = "";
        string genPath = Path.Combine(_root, id.ToString(), id + ".cpp");
        _genOutDir = Path.GetFullPath(Path.Combine(_root, "..", "generated", id.ToString()));
        try
        {
            var cur = GenStorage.GetCurrent(id);
            if (cur.Ok && !string.IsNullOrEmpty(cur.Code))
            {
                GenBox.Text = cur.Code;
                Directory.CreateDirectory(Path.GetDirectoryName(genPath)!);
                File.WriteAllText(genPath, cur.Code);
            }
            else
            {
                GenBox.Text = File.Exists(genPath) ? File.ReadAllText(genPath) : "";
            }
            GenPathText.Text = "生成器代码：" + genPath + "\n数据输出固定目录：" + _genOutDir + "（历史版本存 LSM，最新版本存 MySQL，数据文件仅本地）";

        }
        catch (Exception ex) { GenPathText.Text = "读取生成器失败：" + ex.Message; }
        RefreshGenVersions();
        RefreshGenFiles();
    }
    private void RefreshGenVersions()
    {
        GenVersionList.ItemsSource = null;
        if (_currentId is not int id) return;
        var list = new List<GenVersion>();
        try
        {
            foreach (var v in GenStorage.ListVersions(id))
                list.Add(new GenVersion(v.Version, v.Ts, v.Lines, v.Summary));
        }
        catch { }
        GenVersionList.ItemsSource = list;
        GenVersionList.DisplayMemberPath = "Display";
    }
    private void RefreshGenFiles()
    {
        GenFileList.ItemsSource = null;
        if (_currentId is not int id) { GenFileTitle.Text = "生成的数据文件"; return; }
        var list = new List<GenFile>();
        try
        {
            list = JsonSerializer.Deserialize<List<GenFile>>(AuthorInterop.GenListFiles(id),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { }
        GenFileList.ItemsSource = list;
        GenFileList.DisplayMemberPath = "Display";
        GenFileTitle.Text = $"生成的数据文件（{_genOutDir}）  共 {list.Count} 个";
    }

    private void OnGenSave(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        string code = GenBox.Text;
        int ver = GenStorage.Save(id, code);
        if (ver > 0)
        {
            // 同步写到本地文件供编译
            string genPath = Path.Combine(_root, id.ToString(), id + ".cpp");
            Directory.CreateDirectory(Path.GetDirectoryName(genPath)!);
            File.WriteAllText(genPath, code);
            GenMsg.Text = "已保存生成器代码，归档为版本 v" + ver + "（LSM 历史版本 + MySQL 最新版本）";
            RefreshGenVersions();
        }
        else GenMsg.Text = "保存失败";
    }
    private void OnGenCompile(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        string code = GenBox.Text;
        int ver = GenStorage.Save(id, code);
        if (ver <= 0) { GenMsg.Text = "保存失败"; return; }
        string genPath = Path.Combine(_root, id.ToString(), id + ".cpp");
        Directory.CreateDirectory(Path.GetDirectoryName(genPath)!);
        File.WriteAllText(genPath, code);
        string r = AuthorInterop.GenCompile(id);
        GenMsg.Text = r.Contains("\"ok\":true")
            ? "编译成功 ✓（可设置数量后点「生成数据」）"
            : "编译失败：" + ParseError(r);
        RefreshGenVersions();
    }
        private async void OnGenRun(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        GenMsg.Text = "正在运行生成器……";
        string r = await Task.Run(() => AuthorInterop.GenRun(id));
        try
        {
            using var doc = JsonDocument.Parse(r);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            {
                int gen = root.TryGetProperty("generated", out var g) ? g.GetInt32() : 0;
                int total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                string outDir = root.TryGetProperty("outDir", out var od) ? od.GetString() ?? "" : "";
                var fails = new List<string>();
                if (root.TryGetProperty("fails", out var fa))
                    foreach (var x in fa.EnumerateArray()) fails.Add(x.GetString() ?? "");
                GenMsg.Text = $"完成：成功 {gen}/{total} 个 → {outDir}"
                            + (fails.Count > 0 ? "；失败：" + string.Join("；", fails.Take(5)) : "");
            }
            else GenMsg.Text = "生成失败：" + ParseError(r);
        }
        catch { GenMsg.Text = "生成失败：" + r; }
        RefreshGenFiles();
    }

    private void OnGenImportFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入生成器代码文件",
            Filter = "C++ 源文件 (*.cpp;*.cc;*.cxx)|*.cpp;*.cc;*.cxx|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            string code = System.IO.File.ReadAllText(dlg.FileName);
            GenBox.Text = code;
            GenMsg.Text = "已导入：" + dlg.FileName + "（" + code.Length + " 字符）；点「保存代码」留版本";
        }
        catch (Exception ex)
        {
            GenMsg.Text = "导入失败：" + ex.Message;
        }
    }

    private void OnSelectGenVersion(object sender, SelectionChangedEventArgs e)
    {
        if (GenVersionList.SelectedItem is not GenVersion v || _currentId is not int id) return;
        var ver = GenStorage.GetVersion(id, v.Version);
        GenVersionView.Text = ver.Ok ? ver.Code : "";
    }
    private void OnGenRestore(object sender, RoutedEventArgs e)
    {
        if (GenVersionList.SelectedItem is not GenVersion v || _currentId is not int id)
        {
            GenMsg.Text = "请先在版本列表中选择一个版本";
            return;
        }
        var ver = GenStorage.GetVersion(id, v.Version);
        if (ver.Ok)
        {
            GenBox.Text = ver.Code;
            GenMsg.Text = "已把 v" + v.Version + "（" + v.Ts + "）载入编辑器；点「保存代码」生效，会再产生一个新版本";
        }
    }
    private void OnSelectGenFile(object sender, SelectionChangedEventArgs e)
    {
        if (GenFileList.SelectedItem is not GenFile gf || _currentId is not int id) return;
        string r = AuthorInterop.GenGetFile(id, gf.Name);
        try
        {
            using var doc = JsonDocument.Parse(r);
            var root = doc.RootElement;
            string text = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            bool truncated = root.TryGetProperty("truncated", out var tr) && tr.GetBoolean();
            GenFileView.Text = truncated
                ? text + "\n……（文件过大，仅显示前 200KB，请在资源管理器中查看完整文件）"
                : text;
        }
        catch { GenFileView.Text = ""; }
    }

    private void OnRefreshGenFiles(object sender, RoutedEventArgs e) => RefreshGenFiles();

    private void OnOpenGenDir(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_genOutDir)) return;
        Directory.CreateDirectory(_genOutDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_genOutDir}\"") { UseShellExecute = true });
    }

    private void OnGenImportToProblem(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id || GenFileList.SelectedItem is not GenFile gf)
        {
            GenMsg.Text = "请先在右下数据文件列表中选择一个 .in 文件";
            return;
        }
        string r = AuthorInterop.GenImportToProblem(id, gf.Name);
        if (r.Contains("\"ok\":true"))
        {
            GenMsg.Text = $"已导入 {gf.Name} 到题目测试数据目录；可在「测试数据」页用标程生成对应 .out";
            RefreshDataList();
        }
        else GenMsg.Text = "导入失败：" + ParseError(r);
    }

    // ---------- 跨题查找生成器代码（C++ 后端执行） ----------
    private void OnGenSearch(object sender, RoutedEventArgs e) => DoGenSearch();

    private void OnGenSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoGenSearch();
    }

    private void DoGenSearch()
    {
        string kw = GenSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(kw))
        {
            MessageBox.Show("请输入要查找的关键字（函数名、变量名、注释内容等）", "查找生成器代码");
            return;
        }
        var items = GenStorage.Search(kw);
        if (items.Count == 0)
        {
            MessageBox.Show("没有任何题目的生成器代码包含 " + kw, "查找结果");
            return;
        }
        var hits = new List<SearchHit>();
        foreach (var it in items)
        {
            string title = "P" + it.ProblemId + " v" + it.Version;
            var p = _problems.FirstOrDefault(x => x.Id == it.ProblemId);
            if (p != null) title += " " + p.Title;
            hits.Add(new SearchHit(it.ProblemId, title, 1, it.Preview, kw));
        }
        var w = new GenSearchWindow(hits, kw) { Owner = this };
        if (w.ShowDialog() == true && w.Selected is SearchHit h)
        {
            SelectProblem(h.Pid);
            Tabs.SelectedItem = GenTab;
            GenMsg.Text = "已跳转到 P" + h.Pid + "，关键字 " + kw + " 匹配";
        }
    }
    private void RefreshDataList()
    {
        if (_currentId is not int id) { DataList.ItemsSource = null; return; }
        string dir = ProblemDir(id);
        var items = new List<string>();
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir, "*.in").OrderBy(f => f))
            {
                string baseName = Path.GetFileNameWithoutExtension(f);
                bool hasOut = File.Exists(Path.Combine(dir, baseName + ".out"));
                items.Add($"{baseName}.in ⇄ {baseName}.out  {(hasOut ? "✓" : "✗ 缺答案")}");
            }
        }
        DataList.ItemsSource = items;
    }

    // ---------- 题面 ----------
    private void OnSaveStatement(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        string r = AuthorInterop.SaveStatement(id, TitleBox.Text, DescBox.Text, SampleInBox.Text, SampleOutBox.Text);
        if (!r.Contains("\"ok\":true"))
        {
            StatementMsg.Text = ParseError(r);
            return;
        }
        // 保存时间/内存限制 + 标签（逗号分隔 → JSON 数组）
        int timeMs = int.TryParse(TimeLimitBox.Text.Trim(), out var t) ? t : 1000;
        int memMb = int.TryParse(MemLimitBox.Text.Trim(), out var m) ? m : 256;
        var tags = TagsBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string tagsJson = "[" + string.Join(",", tags.Select(t => $"\"{t.Replace("\"", "")}\"")) + "]";
        string rm = AuthorInterop.SaveMeta(id, timeMs, memMb, tagsJson);
        if (rm.Contains("\"ok\":true"))
        {
            StatementMsg.Text = $"已保存（{timeMs}ms / {memMb}MB / {string.Join(",", tags)}）";
            RefreshList();
        }
        else StatementMsg.Text = ParseError(rm);
    }

    // ---------- 历史版本 ----------
    private void RefreshHistory()
    {
        HistoryList.ItemsSource = null;
        HistoryDetail.Text = "";
        if (_currentId is not int id) return;
        var items = new List<HistoryItem>();
        try
        {
            items = JsonSerializer.Deserialize<List<HistoryItem>>(AuthorInterop.GetHistory(id),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { }
        HistoryList.ItemsSource = items;
        HistoryList.DisplayMemberPath = "Display";
    }

    private void OnSelectHistory(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItem h || _currentId is not int id) return;
        // 从题库 history 快照目录读该版本题面 / 样例 / 元数据
        string vdir = Path.Combine(_root, id.ToString(), "history", h.Version.ToString());
        string st = File.Exists(Path.Combine(vdir, "statement.txt"))
            ? File.ReadAllText(Path.Combine(vdir, "statement.txt")) : "";
        int nl = st.IndexOf('\n');
        string title = nl < 0 ? st : st[..nl];
        string desc = nl < 0 ? "" : st[(nl + 1)..];
        string sampleIn = File.Exists(Path.Combine(vdir, "sample.in"))
            ? File.ReadAllText(Path.Combine(vdir, "sample.in")) : "";
        string sampleOut = File.Exists(Path.Combine(vdir, "sample.out"))
            ? File.ReadAllText(Path.Combine(vdir, "sample.out")) : "";
        HistoryDetail.Text = $"【历史版本 v{h.Version} · {h.UpdatedAt}】\n"
                           + $"标题：{title}\n"
                           + $"限制：{h.TimeLimitMs}ms / {h.MemLimitMB}MB\n"
                           + $"标签：{string.Join(", ", h.Tags)}\n"
                           + "──────────────────────────\n"
                           + desc
                           + "\n──────────────────────────\n"
                           + $"样例输入：\n{sampleIn}\n"
                           + $"样例输出：\n{sampleOut}";
    }

    // ---------- 测试数据 ----------
    private void OnImportIn(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "输入文件 (*.in)|*.in|所有文件 (*.*)|*.*", Title = "选择测试输入文件（可多选）" };
        if (dlg.ShowDialog() != true) return;
        string dir = ProblemDir(id);
        int n = 0;
        foreach (var f in dlg.FileNames)
        {
            string dst = Path.Combine(dir, Path.GetFileName(f));
            File.Copy(f, dst, true);
            n++;
        }
        DataMsg.Text = $"已导入 {n} 个 .in 文件";
        RefreshDataList();
    }

    private void OnImportOut(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "答案文件 (*.out)|*.out|所有文件 (*.*)|*.*", Title = "选择答案文件（可多选）" };
        if (dlg.ShowDialog() != true) return;
        string dir = ProblemDir(id);
        int n = 0;
        foreach (var f in dlg.FileNames)
        {
            string dst = Path.Combine(dir, Path.GetFileName(f));
            File.Copy(f, dst, true);
            n++;
        }
        DataMsg.Text = $"已导入 {n} 个 .out 文件";
        RefreshDataList();
    }

    private void OnGenOutputs(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        string stdExe = Path.Combine(_tempRoot, id.ToString(), "std.exe");
        if (!File.Exists(stdExe))
        {
            DataMsg.Text = "请先在「标程」页保存并编译标程";
            return;
        }
        string r = AuthorInterop.GenOutputs(id, stdExe);
        // 解析结果展示
        try
        {
            using var doc = JsonDocument.Parse(r);
            var results = doc.RootElement.GetProperty("results");
            var lines = new List<string>();
            foreach (var x in results.EnumerateArray())
                lines.Add($"{x.GetProperty("file").GetString()} → {x.GetProperty("status").GetString()} ({x.GetProperty("ms").GetInt32()}ms)");
            DataMsg.Text = string.Join("\n", lines);
        }
        catch { DataMsg.Text = r; }
        RefreshDataList();
    }

    private void OnDeleteData(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id || DataList.SelectedItem is not string sel) return;
        string baseName = sel.Split('⇄')[0].Trim();
        string dir = ProblemDir(id);
        string inF = Path.Combine(dir, baseName);
        string outF = Path.Combine(dir, Path.GetFileNameWithoutExtension(baseName) + ".out");
        if (File.Exists(inF)) File.Delete(inF);
        if (File.Exists(outF)) File.Delete(outF);
        DataMsg.Text = $"已删除 {baseName}";
        RefreshDataList();
    }

    // ---------- 标程 / spj ----------
    private void OnCompileStd(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        string dir = ProblemDir(id);
        string src = Path.Combine(dir, "std.cpp");
        File.WriteAllText(src, StdBox.Text);
        string exe = Path.Combine(_tempRoot, id.ToString(), "std.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        string r = AuthorInterop.Compile(src, exe);
        StdMsg.Text = r.Contains("\"ok\":true")
            ? "编译成功 ✓（可去「测试数据」页运行生成答案）"
            : "编译失败：" + ParseError(r);
    }

    private void OnCompileSpj(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        string dir = ProblemDir(id);
        string src = Path.Combine(dir, "spj.cpp");
        File.WriteAllText(src, SpjBox.Text);
        string exe = Path.Combine(_tempRoot, id.ToString(), "spj.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        string r = AuthorInterop.Compile(src, exe);
        SpjMsg.Text = r.Contains("\"ok\":true") ? "编译成功 ✓" : "编译失败：" + ParseError(r);
    }

    // ---------- 发布 ----------
    private void OnValidate(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        string r = AuthorInterop.Validate(id);
        try
        {
            using var doc = JsonDocument.Parse(r);
            var root = doc.RootElement;
            var miss = root.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ToList();
            string s = root.GetProperty("inCount").GetInt32() + " 组数据"
                     + (root.GetProperty("hasStd").GetBoolean() ? "，有标程" : "，无标程")
                     + (root.GetProperty("hasSpj").GetBoolean() ? "，有 spj" : "")
                     + (miss.Count > 0 ? "；缺失：" + string.Join("、", miss) : "；完整 ✓");
            PublishMsg.Text = s;
        }
        catch { PublishMsg.Text = r; }
    }

    private void OnPublish(object sender, RoutedEventArgs e)
    {
        if (_currentId is not int id) return;
        string target = TargetBox.Text.Trim();
        if (string.IsNullOrEmpty(target))
        {
            PublishMsg.Text = "请填写目标目录";
            return;
        }
        string r = AuthorInterop.Publish(id, target);
        if (r.Contains("\"ok\":true"))
        {
            int ver = 0;
            try { using var doc = JsonDocument.Parse(r); ver = doc.RootElement.GetProperty("version").GetInt32(); } catch { }
            PublishMsg.Text = $"已发布到 {target}\\{id} ✓（版本 v{ver}，客户端启动后即可看到新题）";
            RefreshHistory();
        }
        else PublishMsg.Text = "发布失败：" + ParseError(r);
    }

    // ---------- 比赛 ----------
    private void RefreshContests()
    {
        var items = new List<ContestInfo>();
        try
        {
            items = JsonSerializer.Deserialize<List<ContestInfo>>(AuthorInterop.ContestList(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch { }
        ContestList.ItemsSource = items;
        ContestList.DisplayMemberPath = "Display";
    }

    private void OnContestSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ContestIdBox.Text.Trim(), out int cid) || cid <= 0)
        {
            MessageBox.Show("请输入合法的比赛编号（正整数）");
            return;
        }
        string name = ContestNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("请输入比赛名称");
            return;
        }
        string start = ContestStartBox.Text.Trim();
        string end = ContestEndBox.Text.Trim();
        // 题目 IDs：逗号分隔 → JSON 数组 [1,2,3]
        var ids = ContestProblemsBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var validIds = ids.Where(s => int.TryParse(s, out _)).ToList();
        string problemsJson = "[" + string.Join(",", validIds) + "]";
        string r = AuthorInterop.ContestCreate(cid, name, "", start, end, problemsJson);
        if (r.Contains("\"ok\":true"))
        {
            ContestDetailBox.Text = $"已保存比赛 C{cid}：{name}\n时间：{start} ~ {end}\n题目：{string.Join(", ", validIds)}";
            RefreshContests();
        }
        else Status(ParseError(r));
    }

    private void OnContestPublish(object sender, RoutedEventArgs e)
    {
        if (ContestList.SelectedItem is not ContestInfo c)
        {
            MessageBox.Show("请先在左侧选择要发布的比赛");
            return;
        }
        string target = ContestTargetBox.Text.Trim();
        if (string.IsNullOrEmpty(target))
        {
            MessageBox.Show("请填写目标目录");
            return;
        }
        string r = AuthorInterop.ContestPublish(c.Id, target);
        if (r.Contains("\"ok\":true"))
        {
            ContestDetailBox.Text = $"已发布比赛 C{c.Id} 到 {target}\\contests\\{c.Id} ✓";
            Status($"比赛 C{c.Id} 已发布");
        }
        else ContestDetailBox.Text = "发布失败：" + ParseError(r);
    }

    private void OnSelectContest(object sender, SelectionChangedEventArgs e)
    {
        if (ContestList.SelectedItem is not ContestInfo c) return;
        try
        {
            using var doc = JsonDocument.Parse(AuthorInterop.ContestGet(c.Id));
            var root = doc.RootElement;
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string desc = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            string start = root.TryGetProperty("startTime", out var s) ? s.GetString() ?? "" : "";
            string end = root.TryGetProperty("endTime", out var en) ? en.GetString() ?? "" : "";
            string problems = root.TryGetProperty("problems", out var p) ? p.GetRawText() : "[]";
            ContestDetailBox.Text = $"比赛 C{c.Id}：{name}\n时间：{start} ~ {end}\n题目 IDs：{problems}\n\n{desc}";
        }
        catch (Exception ex)
        {
            ContestDetailBox.Text = "读取比赛详情失败：" + ex.Message;
        }
    }

    private static string ParseError(string json)
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
