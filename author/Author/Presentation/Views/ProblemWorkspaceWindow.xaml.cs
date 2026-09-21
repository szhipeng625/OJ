using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using author.Business.Services;
using author.DataAccess.Models;
using author.Presentation.Helpers;
using Microsoft.Win32;

namespace author.Presentation.Views;

/// <summary>
/// 单题工作台窗口：一道题一个窗口，可同时打开多个（不同题目并行编译/评测）。
/// 本类只处理界面事件，业务全部在 BLL 服务，数据访问全部在 DAL。
/// </summary>
public partial class ProblemWorkspaceWindow : Window
{
    private readonly int _id;
    private readonly Workbench _wb;
    private readonly WorkspaceManager _mgr;
    private readonly string _testDataDir;
    private readonly string _serverProblemDir;
    private string _genOutDir = "";
    private string _genName = "";

    public ProblemWorkspaceWindow(int problemId, Workbench workbench, WorkspaceManager manager,
        string testDataDir = "", string serverProblemDir = "")
    {
        InitializeComponent();
        _id = problemId;
        _wb = workbench;
        _mgr = manager;
        _testDataDir = testDataDir;
        _serverProblemDir = serverProblemDir;
        Title = $"P{problemId} 题目工作台";
        TargetBox.Text = string.IsNullOrWhiteSpace(serverProblemDir) ? @"D:\OJ\server\problems" : serverProblemDir;
        Loaded += async (_, _) => await LoadAllAsync();
    }

    // 统一的忙碌遮罩：同一窗口的任务串行（BLL 内还有按题号信号量兜底）
    private bool _busy;
    private async Task RunBusy(string text, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Tabs.IsEnabled = false;
        BusyText.Text = text;
        BusyMask.Visibility = Visibility.Visible;
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            BusyMask.Visibility = Visibility.Collapsed;
            Tabs.IsEnabled = true;
            _busy = false;
        }
    }

    private async Task LoadAllAsync()
    {
        await RunBusy("正在载入题目…", async () =>
        {
            var c = await _wb.Problems.LoadAsync(_id);
            if (!IsLoaded) return;
            TitleBox.Text = c.StatementTitle;
            DescBox.Text = c.StatementDesc;
            SampleInBox.Text = c.SampleIn;
            SampleOutBox.Text = c.SampleOut;
            TimeLimitBox.Text = c.Meta.TimeLimitMs.ToString();
            MemLimitBox.Text = c.Meta.MemLimitMB.ToString();
            TagsBox.Text = string.Join(",", c.Meta.Tags);
            StdBox.Text = c.StdCode;
            StatementMsg.Text = StdMsg.Text = DataMsg.Text = PublishMsg.Text = "";
            await RefreshDataList();
            await LoadGenAsync();
        });
    }

    // ---------- 题面 ----------
    private async void OnSaveStatement(object sender, RoutedEventArgs e)
    {
        int timeMs = int.TryParse(TimeLimitBox.Text.Trim(), out var t) ? t : 1000;
        int memMb = int.TryParse(MemLimitBox.Text.Trim(), out var m) ? m : 256;
        var tags = TagsBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await RunBusy("正在保存题面…", async () =>
        {
            var r = await _wb.Problems.SaveStatementAsync(
                _id, TitleBox.Text, DescBox.Text, SampleInBox.Text, SampleOutBox.Text,
                timeMs, memMb, tags);
            if (IsLoaded) StatementMsg.Text = r.Message;
            _mgr.NotifyProblemListChanged();
        });
    }

    // ---------- 测试数据 ----------
    private async Task RefreshDataList()
    {
        var rows = await _wb.Problems.ListGroupedRowsAsync(_id);
        if (!IsLoaded) return;
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DataRow.GroupTitle)));
        DataGroupView.ItemsSource = view;
    }

    private async void OnAutoFill(object sender, RoutedEventArgs e)
    {
        await RunBusy("正在自动搜索填充测试数据…", async () =>
        {
            var (inCnt, outCnt) = await _wb.Problems.AutoFillDataAsync(_id, _testDataDir);
            if (IsLoaded)
                DataMsg.Text = inCnt > 0
                    ? $"自动填充完成：{inCnt} 个 .in / {outCnt} 个 .out（源目录：{_testDataDir}）"
                    : $"未在测试数据目录找到 .in/.out：{_testDataDir}";
            await RefreshDataList();
            _mgr.NotifyProblemListChanged();
        });
    }

    private async void OnImportIn(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "输入文件 (*.in)|*.in|所有文件 (*.*)|*.*", Title = "选择测试输入文件（可多选）" };
        if (dlg.ShowDialog() != true) return;
        await RunBusy("正在导入…", async () =>
        {
            int n = await _wb.Problems.ImportDataAsync(_id, dlg.FileNames);
            if (IsLoaded) DataMsg.Text = $"已导入 {n} 个 .in 文件";
            await RefreshDataList();
            _mgr.NotifyProblemListChanged();
        });
    }

    private async void OnImportOut(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "答案文件 (*.out)|*.out|所有文件 (*.*)|*.*", Title = "选择测试答案文件（可多选）" };
        if (dlg.ShowDialog() != true) return;
        await RunBusy("正在导入…", async () =>
        {
            int n = await _wb.Problems.ImportDataAsync(_id, dlg.FileNames);
            if (IsLoaded) DataMsg.Text = $"已导入 {n} 个 .out 文件";
            await RefreshDataList();
            _mgr.NotifyProblemListChanged();
        });
    }

    private async void OnGenerateAnswers(object sender, RoutedEventArgs e)
    {
        await RunBusy("正在自动搜索并生成标准答案…", async () =>
        {
            var r = await _wb.Problems.GenerateAnswersAsync(_id, StdBox.Text, _testDataDir);
            if (IsLoaded) DataMsg.Text = r.Message;
            await RefreshDataList();
            _mgr.NotifyProblemListChanged();
        });
    }

    private async void OnDataRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGroupView.SelectedItem is not DataRow row) return;
        var (inText, inTrunc) = await _wb.Problems.ReadDataFileAsync(_id, row.GroupKey, row.InFile);
        var (outText, outTrunc) = await _wb.Problems.ReadDataFileAsync(_id, row.GroupKey, row.OutFile);
        if (!IsLoaded) return;
        var win = new DataCompareWindow(row, inText, inTrunc, outText, outTrunc) { Owner = this };
        win.Show();
    }

    private async void OnDeleteData(object sender, RoutedEventArgs e)
    {
        if (DataGroupView.SelectedItem is not DataRow row) return;
        await RunBusy("正在删除…", async () =>
        {
            await _wb.Problems.DeleteDataAsync(_id, row.GroupKey, row.BaseName);
            if (IsLoaded) DataMsg.Text = $"已删除 {row.BaseName}（{row.GroupTitle}）";
            await RefreshDataList();
            _mgr.NotifyProblemListChanged();
        });
    }

    // ---------- 标程 ----------
    private async void OnCompileStd(object sender, RoutedEventArgs e)
    {
        string code = StdBox.Text;
        await RunBusy("正在编译标程（g++）…", async () =>
        {
            var r = await _wb.Problems.CompileStdAsync(_id, code);
            if (IsLoaded) StdMsg.Text = r.Message;
        });
    }

    // ---------- 发布 ----------
    private async void OnValidate(object sender, RoutedEventArgs e)
    {
        await RunBusy("正在校验题目完整性…", async () =>
        {
            var msg = await _wb.Problems.ValidateAsync(_id);
            if (IsLoaded) PublishMsg.Text = msg;
        });
    }

    private async void OnPublish(object sender, RoutedEventArgs e)
    {
        string target = TargetBox.Text.Trim();
        await RunBusy("正在发布到客户端题目目录…", async () =>
        {
            var r = await _wb.Problems.PublishAsync(_id, target);
            if (IsLoaded) PublishMsg.Text = r.Message;
            _mgr.NotifyProblemListChanged();
        });
    }

    // ---------- 数据生成（多生成器） ----------
    // 列出本课题下所有生成器；默认选中第一个并加载
    private async Task LoadGenAsync(bool selectFirst = true)
    {
        var gens = await _wb.Generators.ListAsync(_id);
        if (!IsLoaded) return;
        GenList.ItemsSource = gens;
        GenList.DisplayMemberPath = nameof(GenSummary.Display);
        GenFileView.Text = "";
        GenMsg.Text = "";
        GenList.SelectedIndex = -1;
        if (selectFirst && gens.Count > 0)
        {
            GenList.SelectedIndex = 0;              // 触发 OnSelectGenerator（_busy 中会被跳过）
            if (_busy) await LoadSelectedGeneratorAsync();
        }
        else
        {
            _genName = "";
            GenBox.Text = "";
            GenDescBox.Text = "";
            GenPathText.Text = "";
            GenFileList.ItemsSource = null;
            GenFileTitle.Text = "生成的数据文件";
        }
    }

    // 选中某生成器后：加载其代码 / 描述 / 路径 / 版本 / 数据文件
    private async Task LoadSelectedGeneratorAsync()
    {
        if (GenList.SelectedItem is not GenSummary g)
        {
            _genName = "";
            GenBox.Text = "";
            GenDescBox.Text = "";
            GenPathText.Text = "";
            GenFileList.ItemsSource = null;
            GenFileTitle.Text = "生成的数据文件";
            return;
        }
        _genName = g.Name;
        var ws = await _wb.Generators.LoadAsync(_id, _genName);
        if (!IsLoaded) return;
        GenBox.Text = ws.Code;
        GenDescBox.Text = ws.Desc;
        _genOutDir = ws.OutDir;
        GenPathText.Text = ws.GenPath;
        await RefreshGenFiles();
    }


    private async Task RefreshGenFiles()
    {
        if (string.IsNullOrEmpty(_genName)) { GenFileList.ItemsSource = null; return; }
        var list = await _wb.Generators.FilesAsync(_id, _genName);
        if (!IsLoaded) return;
        GenFileList.ItemsSource = list;
        GenFileList.DisplayMemberPath = nameof(GenFile.Display);
        GenFileTitle.Text = $"生成的数据文件（{_genOutDir}）  共 {list.Count} 个";
    }

    // 新建生成器（名字 + 描述取自顶部两个输入框）
    private async void OnNewGenerator(object sender, RoutedEventArgs e)
    {
        string name = GenNewNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            GenMsg.Text = "请先在右侧输入生成器名称（如 juhua / lian，字母数字下划线）";
            return;
        }
        string desc = GenNewDescBox.Text.Trim();
        await RunBusy("正在创建生成器…", async () =>
        {
            var r = await _wb.Generators.CreateAsync(_id, name, desc);
            if (IsLoaded) GenMsg.Text = r.Message;
            await LoadGenAsync(selectFirst: false);
            int idx = -1;
            for (int i = 0; i < GenList.Items.Count; i++)
                if (GenList.Items[i] is GenSummary gs && gs.Name == name) { idx = i; break; }
            if (idx >= 0)
            {
                GenList.SelectedIndex = idx;       // _busy 中事件会被跳过
                if (_busy) await LoadSelectedGeneratorAsync();
            }
        });
    }

    // 复刷生成器列表
    private async void OnRefreshGenerators(object sender, RoutedEventArgs e) => await LoadGenAsync();

    // 在列表中选中某个生成器
    private async void OnSelectGenerator(object sender, SelectionChangedEventArgs e)
    {
        if (_busy) return;
        await LoadSelectedGeneratorAsync();
    }

    // 保存生成器描述
    private async void OnGenSaveDesc(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_genName)) { GenMsg.Text = "请先选择一个生成器"; return; }
        string keep = _genName;
        await RunBusy("正在保存描述…", async () =>
        {
            var r = await _wb.Generators.SetDescAsync(_id, keep, GenDescBox.Text.Trim());
            if (IsLoaded) GenMsg.Text = r.Message;
            await LoadGenAsync(selectFirst: false);
            int idx = -1;
            for (int i = 0; i < GenList.Items.Count; i++)
                if (GenList.Items[i] is GenSummary gs && gs.Name == keep) { idx = i; break; }
            if (idx >= 0)
            {
                GenList.SelectedIndex = idx;
                if (_busy) await LoadSelectedGeneratorAsync();
            }
        });
    }

    private async void OnGenSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_genName)) { GenMsg.Text = "请先选择一个生成器"; return; }
        string code = GenBox.Text;
        await RunBusy("正在保存生成器代码…", async () =>
        {
            var r = await _wb.Generators.SaveAsync(_id, _genName, code);
            if (IsLoaded) GenMsg.Text = r.Message;
        });
    }

    private async void OnGenCompile(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_genName)) { GenMsg.Text = "请先选择一个生成器"; return; }
        string code = GenBox.Text;
        await RunBusy("正在编译生成器（g++）…", async () =>
        {
            var r = await _wb.Generators.CompileAsync(_id, _genName, code);
            if (IsLoaded) GenMsg.Text = r.Message;
        });
    }

    private async void OnGenRun(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_genName)) { GenMsg.Text = "请先选择一个生成器"; return; }
        int n = int.TryParse(GenCountBox.Text.Trim(), out var g) && g > 0 ? g : 10;
        await RunBusy("正在保存并运行生成器…", async () =>
        {
            var sv = await _wb.Generators.SaveAsync(_id, _genName, GenBox.Text);
            if (!sv.Ok) { if (IsLoaded) GenMsg.Text = sv.Message; return; }
            var r = await _wb.Generators.RunAsync(_id, _genName, n);
            if (!r.Ok)
            {
                if (IsLoaded) GenMsg.Text = r.Message;
                await RefreshGenFiles();
                return;
            }
            await RefreshGenFiles();

            // 后台自动运行标程，把标准程序输出重定向到对应的 .out 文件位置
            string ans;
            if (File.Exists(_wb.Problems.StdExePath(_id)))
            {
                var gr = await _wb.Problems.GenOutputsAsync(_id);
                ans = gr.Message;
            }
            else
            {
                ans = "（标程尚未编译，未自动生成 .out；可到「生成标准答案」页一键生成）";
            }
            if (IsLoaded) GenMsg.Text = r.Message + "\n" + ans;
            _mgr.NotifyProblemListChanged();
        });
    }


    private async void OnSelectGenFile(object sender, SelectionChangedEventArgs e)
    {
        if (GenFileList.SelectedItem is not GenFile gf) return;
        if (string.IsNullOrEmpty(_genName)) return;
        var (text, truncated) = await _wb.Generators.GetFileAsync(_id, _genName, gf.Name);
        if (!IsLoaded) return;
        GenFileView.Text = truncated
            ? text + "\n……（文件过大，仅显示前 200KB，请在资源管理器中查看完整文件）"
            : text;
    }

    private async void OnRefreshGenFiles(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_genName)) { GenMsg.Text = "请先选择一个生成器"; return; }
        await RefreshGenFiles();
    }

    private void OnOpenGenDir(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_genOutDir)) return;
        Directory.CreateDirectory(_genOutDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_genOutDir}\"") { UseShellExecute = true });
    }

    private async void OnGenImportToProblem(object sender, RoutedEventArgs e)
    {
        if (GenFileList.SelectedItem is not GenFile gf)
        {
            GenMsg.Text = "请先在右下数据文件列表中选择一个 .in 文件";
            return;
        }
        if (string.IsNullOrEmpty(_genName)) return;
        await RunBusy("正在导入到题目测试数据…", async () =>
        {
            var r = await _wb.Generators.ImportToProblemAsync(_id, _genName, gf.Name);
            if (IsLoaded) GenMsg.Text = r.Message;
            await RefreshDataList();
            _mgr.NotifyProblemListChanged();
        });
    }

    // ---------- 跨题查找 ----------
    private void OnGenSearch(object sender, RoutedEventArgs e) => DoGenSearch();
    private void OnGenSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoGenSearch();
    }

    private async void DoGenSearch()
    {
        string kw = GenSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(kw))
        {
            MessageBox.Show("请输入要查找的关键字（函数名、变量名、注释内容等）", "查找生成器代码");
            return;
        }
        List<SearchHit> hits;
        try
        {
            var problems = await _wb.Problems.ListAsync();
            hits = await _wb.Generators.SearchAsync(kw, problems);
        }
        catch (Exception ex)
        {
            MessageBox.Show("查找失败：" + ex.Message);
            return;
        }
        if (hits.Count == 0)
        {
            MessageBox.Show("没有任何题目的生成器代码包含 " + kw, "查找结果");
            return;
        }
        var w = new GenSearchWindow(hits, kw) { Owner = this };
        if (w.ShowDialog() == true && w.Selected is SearchHit h)
        {
            _mgr.Open(h.Pid);   // 多窗口：跳转到对应题目的工作台（已打开则激活）
        }
    }
}
