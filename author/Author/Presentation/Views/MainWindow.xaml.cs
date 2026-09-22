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

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        if (ProblemList.SelectedItem is ProblemInfo p)
            _workspaces.Open(p.Id);
    }

    // ---------- 比赛管理（独立窗口） ----------
    private void OnOpenContestManagement(object sender, RoutedEventArgs e)
    {
        var win = new ContestManagementWindow(_wb) { Owner = this };
        win.ShowDialog();
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
