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
    private int _genId;
    private string _genName = "";
    private List<GenCheckItem> _genItems = new();

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
        TargetBox.Text = string.IsNullOrWhiteSpace(serverProblemDir) ? "server\\problems" : serverProblemDir;
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

    // ---------- 样例导入（不再手写，从文件导入） ----------
    private void OnImportSampleIn(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "样例输入 (*.in;*.txt)|*.in;*.txt|所有文件 (*.*)|*.*",
            Title = "选择样例输入文件"
        };
        if (dlg.ShowDialog() != true) return;
        SampleInBox.Text = File.ReadAllText(dlg.FileName);
    }

    private void OnImportSampleOut(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "样例输出 (*.out;*.txt)|*.out;*.txt|所有文件 (*.*)|*.*",
            Title = "选择样例输出文件"
        };
        if (dlg.ShowDialog() != true) return;
        SampleOutBox.Text = File.ReadAllText(dlg.FileName);
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
        bool isPublic = PublicBox.IsChecked != false;
        await RunBusy("正在发布到客户端题目目录…", async () =>
        {
            var r = await _wb.Problems.PublishAsync(_id, target, isPublic);
            if (IsLoaded) PublishMsg.Text = r.Message;
            _mgr.NotifyProblemListChanged();
        });
    }

    // ---------- 数据生成器（全局库） ----------
    // 进入页面：从库加载全部生成器（勾选=本题使用），右侧为本题已绑定的生成器
    private async Task LoadGenAsync()
    {
        var lib = await _wb.Generators.ListAsync();
        var used = await _wb.Generators.ListUsedAsync(_id);
        if (!IsLoaded) return;

        var usedIds = new HashSet<int>(used.Select(u => u.Id));
        _genItems = lib
            .Select(g => new GenCheckItem(g.Id, g.Name, g.Desc, usedIds.Contains(g.Id)))
            .ToList();
        foreach (var it in _genItems)
        {
            it.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GenCheckItem.IsUsed)) OnGenItemToggled(it);
            };
        }
        GenList.ItemsSource = _genItems;
        UpdateUsedList();
        GenMsg.Text = "";
        CloseGenEditor();
    }

    private void UpdateUsedList()
    {
        var used = _genItems.Where(x => x.IsUsed).ToList();
        UsedGenList.ItemsSource = used;
        GenUsedTitle.Text = used.Count > 0 ? $"本题使用的数据生成器（{used.Count} 个）" : "本题使用的数据生成器";
    }

    // 勾选变化：刷新右侧列表并绑定/解绑到本题
    private async void OnGenItemToggled(GenCheckItem item)
    {
        UpdateUsedList();
        int n = int.TryParse(GenCountBox.Text.Trim(), out var g) && g > 0 ? g : 10;
        var r = item.IsUsed
            ? await _wb.Generators.BindAsync(_id, item.Id, n)
            : await _wb.Generators.UnbindAsync(_id, item.Id);
        if (IsLoaded) GenMsg.Text = r.Message;
    }

    // 打开某生成器的代码编辑器
    private async Task OpenGenEditorAsync(int id, string name)
    {
        _genId = id;
        _genName = name;
        var d = await _wb.Generators.LoadAsync(id);
        if (!IsLoaded || d == null) return;
        GenBox.Text = d.Code;
        GenDescBox.Text = d.Desc;
        GenPathText.Text = d.Name;
        GenEditPanel.Visibility = Visibility.Visible;
    }

    private void CloseGenEditor()
    {
        _genId = 0;
        _genName = "";
        GenBox.Text = "";
        GenDescBox.Text = "";
        GenPathText.Text = "";
        GenEditPanel.Visibility = Visibility.Collapsed;
    }

    private void OnCloseGenEdit(object sender, RoutedEventArgs e) => CloseGenEditor();

    // 双击左侧条目打开编辑器
    private async void OnGenListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GenList.SelectedItem is GenCheckItem g)
            await OpenGenEditorAsync(g.Id, g.Name);
    }

    // 新建生成器：弹窗命名+描述，保存后关闭；自动勾选并打开编辑器
    private async void OnNewGenerator(object sender, RoutedEventArgs e)
    {
        var win = new NewGeneratorWindow(name => _genItems.Any(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) { Owner = this };
        if (win.ShowDialog() != true || win.GenName is not { } name) return;

        string desc = win.GenDesc ?? "";
        await RunBusy("正在创建生成器…", async () =>
        {
            var r = await _wb.Generators.CreateAsync(name, desc);
            if (!r.Ok)
            {
                if (IsLoaded) GenMsg.Text = r.Message;
                return;
            }
            await LoadGenAsync();
            var item = _genItems.FirstOrDefault(x => x.Id == r.Id);
            if (item != null) item.IsUsed = true;   // 触发 OnGenItemToggled：绑定到本题
            await OpenGenEditorAsync(r.Id, name);
            if (IsLoaded) GenMsg.Text = r.Message;
        });
    }

    // 复刷生成器列表
    private async void OnRefreshGenerators(object sender, RoutedEventArgs e) => await LoadGenAsync();

    // 保存生成器描述
    private async void OnGenSaveDesc(object sender, RoutedEventArgs e)
    {
        if (_genId <= 0) { GenMsg.Text = "请先双击一个生成器打开编辑器"; return; }
        int keepId = _genId;
        string keepName = _genName;
        await RunBusy("正在保存描述…", async () =>
        {
            var r = await _wb.Generators.SetDescAsync(keepId, GenDescBox.Text.Trim());
            if (IsLoaded) GenMsg.Text = r.Message;
            await LoadGenAsync();
            await OpenGenEditorAsync(keepId, keepName);
        });
    }

    private async void OnGenSave(object sender, RoutedEventArgs e)
    {
        if (_genId <= 0) { GenMsg.Text = "请先双击一个生成器打开编辑器"; return; }
        string code = GenBox.Text;
        await RunBusy("正在保存生成器代码…", async () =>
        {
            var r = await _wb.Generators.SaveAsync(_genId, code);
            if (IsLoaded) GenMsg.Text = r.Message;
        });
    }

    private async void OnGenCompile(object sender, RoutedEventArgs e)
    {
        if (_genId <= 0) { GenMsg.Text = "请先双击一个生成器打开编辑器"; return; }
        string code = GenBox.Text;
        await RunBusy("正在编译生成器（g++）…", async () =>
        {
            var r = await _wb.Generators.CompileAsync(_genId, _genName, code);
            if (IsLoaded) GenMsg.Text = r.Message;
        });
    }

    // 生成数据：对勾选的全部生成器运行，弹出窗口展示各生成器的生成情况
    private async void OnGenRun(object sender, RoutedEventArgs e)
    {
        var used = _genItems.Where(x => x.IsUsed).ToList();
        if (used.Count == 0)
        {
            GenMsg.Text = "请先在左侧勾选本题要使用的数据生成器";
            return;
        }
        int n = int.TryParse(GenCountBox.Text.Trim(), out var g) && g > 0 ? g : 10;
        // 确保本题绑定的组数与当前输入一致
        foreach (var it in used) await _wb.Generators.BindAsync(_id, it.Id, n);

        var win = new GeneratorRunWindow(_wb, _id, used.Select(x => (x.Id, x.Name)).ToList(), n) { Owner = this };
        win.Closed += async (_, _) =>
        {
            if (!IsLoaded) return;
            await LoadGenAsync();          // 刷新列表
            await RefreshDataList();
            _mgr.NotifyProblemListChanged();
        };
        win.Show();
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
        List<GenSearchHit> hits;
        try
        {
            hits = await _wb.Generators.SearchAsync(kw);
        }
        catch (Exception ex)
        {
            MessageBox.Show("查找失败：" + ex.Message);
            return;
        }
        if (hits.Count == 0)
        {
            MessageBox.Show("没有生成器代码包含 " + kw, "查找结果");
            return;
        }
        var w = new GenSearchWindow(hits, kw) { Owner = this };
        if (w.ShowDialog() == true && w.Selected is GenSearchHit h)
        {
            await OpenGenEditorAsync(h.Id, h.Name);
        }
    }
}
