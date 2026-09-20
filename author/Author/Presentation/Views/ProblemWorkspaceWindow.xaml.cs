using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private string _genOutDir = "";

    public ProblemWorkspaceWindow(int problemId, Workbench workbench, WorkspaceManager manager)
    {
        InitializeComponent();
        _id = problemId;
        _wb = workbench;
        _mgr = manager;
        Title = $"P{problemId} 题目工作台";
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
            SpjBox.Text = c.SpjCode;
            StatementMsg.Text = StdMsg.Text = SpjMsg.Text = DataMsg.Text = PublishMsg.Text = "";
            await RefreshDataList();
            await RefreshHistory();
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
        var pairs = await _wb.Problems.ListDataAsync(_id);
        if (!IsLoaded) return;
        DataList.ItemsSource = pairs;
        DataList.DisplayMemberPath = nameof(DataPair.Display);
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

    private async void OnGenOutputs(object sender, RoutedEventArgs e)
    {
        await RunBusy("正在运行标程生成答案…", async () =>
        {
            var r = await _wb.Problems.GenOutputsAsync(_id);
            if (IsLoaded) DataMsg.Text = r.Message;
            await RefreshDataList();
        });
    }

    private async void OnDeleteData(object sender, RoutedEventArgs e)
    {
        if (DataList.SelectedItem is not DataPair pair) return;
        await RunBusy("正在删除…", async () =>
        {
            await _wb.Problems.DeleteDataAsync(_id, pair.BaseName);
            if (IsLoaded) DataMsg.Text = $"已删除 {pair.BaseName}";
            await RefreshDataList();
            _mgr.NotifyProblemListChanged();
        });
    }

    // ---------- 标程 / spj ----------
    private async void OnCompileStd(object sender, RoutedEventArgs e)
    {
        string code = StdBox.Text;
        await RunBusy("正在编译标程（g++）…", async () =>
        {
            var r = await _wb.Problems.CompileStdAsync(_id, code);
            if (IsLoaded) StdMsg.Text = r.Message;
        });
    }

    private async void OnCompileSpj(object sender, RoutedEventArgs e)
    {
        string code = SpjBox.Text;
        await RunBusy("正在编译 spj（g++）…", async () =>
        {
            var r = await _wb.Problems.CompileSpjAsync(_id, code);
            if (IsLoaded) SpjMsg.Text = r.Message;
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
            await RefreshHistory();
            _mgr.NotifyProblemListChanged();
        });
    }

    private async Task RefreshHistory()
    {
        var items = await _wb.Problems.HistoryAsync(_id);
        if (!IsLoaded) return;
        HistoryList.ItemsSource = items;
        HistoryList.DisplayMemberPath = nameof(HistoryItem.Display);
        HistoryDetail.Text = "";
    }

    private async void OnSelectHistory(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItem h) return;
        var text = await _wb.Problems.HistoryDetailAsync(_id, h.Version);
        if (IsLoaded) HistoryDetail.Text = text;
    }

    // ---------- 数据生成 ----------
    private async Task LoadGenAsync()
    {
        var ws = await _wb.Generators.LoadAsync(_id);
        if (!IsLoaded) return;
        GenMsg.Text = "";
        GenVersionView.Text = "";
        GenFileView.Text = "";
        GenBox.Text = ws.Code;
        _genOutDir = ws.OutDir;
        GenPathText.Text = ws.PathText;
        GenVersionList.ItemsSource = ws.Versions;
        GenVersionList.DisplayMemberPath = nameof(GenVersion.Display);
        GenFileList.ItemsSource = ws.Files;
        GenFileList.DisplayMemberPath = nameof(GenFile.Display);
        GenFileTitle.Text = $"生成的数据文件（{ws.OutDir}）  共 {ws.Files.Count} 个";
    }

    private async Task RefreshGenVersions()
    {
        var list = await _wb.Generators.VersionsAsync(_id);
        if (!IsLoaded) return;
        GenVersionList.ItemsSource = list;
        GenVersionList.DisplayMemberPath = nameof(GenVersion.Display);
    }

    private async Task RefreshGenFiles()
    {
        var list = await _wb.Generators.FilesAsync(_id);
        if (!IsLoaded) return;
        GenFileList.ItemsSource = list;
        GenFileList.DisplayMemberPath = nameof(GenFile.Display);
        GenFileTitle.Text = $"生成的数据文件（{_genOutDir}）  共 {list.Count} 个";
    }

    private async void OnGenSave(object sender, RoutedEventArgs e)
    {
        string code = GenBox.Text;
        await RunBusy("正在保存生成器代码…", async () =>
        {
            int ver = await _wb.Generators.SaveAsync(_id, code);
            if (IsLoaded)
                GenMsg.Text = ver > 0
                    ? "已保存生成器代码，归档为版本 v" + ver + "（LSM 历史版本 + MySQL 最新版本）"
                    : "保存失败";
            await RefreshGenVersions();
        });
    }

    private async void OnGenCompile(object sender, RoutedEventArgs e)
    {
        string code = GenBox.Text;
        await RunBusy("正在编译生成器（g++）…", async () =>
        {
            var r = await _wb.Generators.CompileAsync(_id, code);
            if (IsLoaded) GenMsg.Text = r.Message;
            await RefreshGenVersions();
        });
    }

    private async void OnGenRun(object sender, RoutedEventArgs e)
    {
        await RunBusy("正在运行生成器产出数据…", async () =>
        {
            var r = await _wb.Generators.RunAsync(_id);
            if (IsLoaded) GenMsg.Text = r.Message;
            await RefreshGenFiles();
        });
    }

    private void OnGenImportFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入生成器代码文件",
            Filter = "C++ 源文件 (*.cpp;*.cc;*.cxx)|*.cpp;*.cc;*.cxx|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            string code = File.ReadAllText(dlg.FileName);
            GenBox.Text = code;
            GenMsg.Text = "已导入：" + dlg.FileName + "（" + code.Length + " 字符）；点「保存代码」留版本";
        }
        catch (Exception ex) { GenMsg.Text = "导入失败：" + ex.Message; }
    }

    private async void OnSelectGenVersion(object sender, SelectionChangedEventArgs e)
    {
        if (GenVersionList.SelectedItem is not GenVersion v) return;
        var code = await _wb.Generators.GetVersionCodeAsync(_id, v.Version);
        if (IsLoaded && code is not null) GenVersionView.Text = code;
    }

    private async void OnGenRestore(object sender, RoutedEventArgs e)
    {
        if (GenVersionList.SelectedItem is not GenVersion v)
        {
            GenMsg.Text = "请先在版本列表中选择一个版本";
            return;
        }
        var code = await _wb.Generators.GetVersionCodeAsync(_id, v.Version);
        if (IsLoaded && code is not null)
        {
            GenBox.Text = code;
            GenMsg.Text = "已把 v" + v.Version + "（" + v.Time + "）载入编辑器；点「保存代码」生效，会再产生一个新版本";
        }
    }

    private async void OnSelectGenFile(object sender, SelectionChangedEventArgs e)
    {
        if (GenFileList.SelectedItem is not GenFile gf) return;
        var (text, truncated) = await _wb.Generators.GetFileAsync(_id, gf.Name);
        if (!IsLoaded) return;
        GenFileView.Text = truncated
            ? text + "\n……（文件过大，仅显示前 200KB，请在资源管理器中查看完整文件）"
            : text;
    }

    private async void OnRefreshGenFiles(object sender, RoutedEventArgs e) => await RefreshGenFiles();

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
        await RunBusy("正在导入到题目测试数据…", async () =>
        {
            var r = await _wb.Generators.ImportToProblemAsync(_id, gf.Name);
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
