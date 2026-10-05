using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using author.Business.Services;
using author.DataAccess.Models;
using author.Presentation.Helpers;

namespace author.Presentation.Views;

/// <summary>
/// 启动器窗口：题库列表（多窗口入口）+ 新建题目（输入标题自动编号 + 自动填充测试数据）。
/// 比赛管理不再直接展示，而是通过右上角「比赛管理」按钮打开独立窗口。
/// 关闭本窗口即退出程序；若仍有后台编译/评测任务，必须等待全部线程结束后才真正退出。
/// </summary>
public partial class MainWindow : Window
{
    private readonly Workbench _wb;
    private readonly WorkspaceManager _workspaces;
    private bool _allowClose;
    private List<ProblemInfo> _problems = new();
    private bool _openingWorkspace;   // 工作台打开中，防止连点开出多个窗口

    public MainWindow(Workbench workbench, WorkspaceManager workspaces)
    {
        InitializeComponent();
        _wb = workbench;
        _workspaces = workspaces;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootText.Text = "题库来源：MySQL 数据库（本地目录仅作新建/编辑时的工作副本缓存）";

        _wb.Build.PendingChanged += OnPendingChanged;
        _workspaces.ProblemListChanged += () => Dispatcher.InvokeAsync(RefreshProblems);
        // Codex 写回题面后，题库列表要跟着刷新
        if (_wb.CodexHost is { } codex)
            codex.ProblemChanged += _ => Dispatcher.InvokeAsync(RefreshProblems);

        await RefreshProblems();
    }

    private void OnPendingChanged(int n)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TaskBadge.Text = n > 0 ? $"后台编译/评测任务：{n} 个进行中…" : "";
            if (_closing)
                StatusText.Text = $"正在等待后台任务完成（剩余 {n} 个），请稍候，完成后将自动退出……";
        });
    }

    // ---------- 题库列表 ----------
    private async Task RefreshProblems()
    {
        // 题库列表直接从 MySQL 拉取（含未公开标记），本地目录仅作新建/编辑时的工作副本缓存
        _problems = await _wb.Problems.ListAsync();
        if (!IsLoaded) return;
        ProblemList.ItemsSource = null;
        ProblemList.ItemsSource = _problems;
        StatusText.Text = $"共 {_problems.Count} 道题";
    }

    // ---------- 新建题目：弹窗输入标题 + 标签 → 自动编号 ----------
    private async void OnNewProblem(object sender, RoutedEventArgs e)
    {
        var win = new NewProblemWindow { Owner = this };
        if (win.ShowDialog() != true || win.ProblemTitle is not { } title) return;

        IsEnabled = false;
        try
        {
            int id = await _wb.Problems.SuggestIdAsync();
            var (ok, msg) = await _wb.Problems.CreateAsync(id, title, win.ProblemTags ?? "");
            StatusText.Text = msg;
            if (!ok) return;

            await RefreshProblems();
            _workspaces.Open(id);   // 新建后直接打开工作台
        }
        catch (Exception ex) { StatusText.Text = "创建失败：" + ex.Message; }
        finally { IsEnabled = true; }
    }

    /// <summary>
    /// 双击题库题目：先亮加载遮罩再开工作台（与客户端同一套 UI）。
    /// 工作台窗口自身的「正在载入题目…」遮罩负责后续的详情加载，这里只覆盖启动器这一段。
    /// </summary>
    private async void OnOpenWorkspace(object sender, EventArgs e)
    {
        if (_openingWorkspace) return;
        if (ProblemList.SelectedItem is not ProblemInfo p) return;

        _openingWorkspace = true;
        BusyOverlay.Message = $"正在打开 P{p.Id} 工作台…";
        BusyOverlay.IsBusy = true;
        try
        {
            // 先让遮罩渲染出来，再做（同步阻塞 UI 线程的）窗口创建
            await Dispatcher.Yield(DispatcherPriority.Background);
            _workspaces.Open(p.Id);
        }
        catch (Exception ex)
        {
            StatusText.Text = "打开失败：" + ex.Message;
        }
        finally
        {
            BusyOverlay.IsBusy = false;
            _openingWorkspace = false;
        }
    }

    // ---------- 比赛管理（独立窗口） ----------
    private void OnOpenContestManagement(object sender, RoutedEventArgs e)
    {
        var win = new ContestManagementWindow(_wb) { Owner = this };
        win.ShowDialog();
    }

    // ---------- ACMDOGCodex：AI 出题助手 ----------
    private void OnOpenCodex(object sender, RoutedEventArgs e)
    {
        var host = _wb.CodexHost ??= new AuthorOjToolHost(_wb);
        var win = new CodexWindow(_wb, host, focusProblemId: null) { Owner = this };
        win.Show();
    }

    // ---------- 退出：等待所有后台线程/任务结束 ----------
    private bool _closing;

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        int pending = _wb.Build.PendingCount;
        if (pending == 0)
        {
            _allowClose = true;
            base.OnClosing(e);
            return;
        }

        // 第一次关闭：拦截，等待全部后台编译/评测任务结束
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        StatusText.Text = $"正在等待 {pending} 个后台编译/评测任务完成，请稍候，完成后将自动退出……";

        await _wb.Build.DrainAsync();
        // 再让一个 Dispatcher 周期跑完，确保任务的界面延续执行完
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

        _workspaces.CloseAll();
        _closing = false;
        _allowClose = true;
        IsEnabled = true;
        Application.Current.Shutdown();
    }
}
