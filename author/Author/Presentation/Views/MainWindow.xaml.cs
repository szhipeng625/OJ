using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using author.Business.Services;
using author.DataAccess.Models;
using author.Presentation.Helpers;

namespace author.Presentation.Views;

/// <summary>
/// 启动器窗口：题目列表（多窗口入口）+ 比赛管理 + 全局后台任务状态。
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
        RootText.Text = "题库目录：" + _wb.Problems.Root;

        _wb.Build.PendingChanged += OnPendingChanged;
        _workspaces.ProblemListChanged += () => Dispatcher.InvokeAsync(RefreshProblems);

        await RefreshProblems();
        await RefreshContests();
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

    // ---------- 题目列表 ----------
    private async Task RefreshProblems()
    {
        _problems = await _wb.Problems.ListAsync();
        if (!IsLoaded) return;
        ProblemList.ItemsSource = null;
        ProblemList.ItemsSource = _problems;
        StatusText.Text = $"共 {_problems.Count} 道题";
    }

    private async void OnNewProblem(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NewIdBox.Text.Trim(), out int id) || id <= 0)
        {
            StatusText.Text = "请输入合法的题目编号（正整数）";
            return;
        }
        IsEnabled = false;
        try
        {
            var (ok, msg) = await _wb.Problems.CreateAsync(id);
            StatusText.Text = msg;
            if (ok)
            {
                await RefreshProblems();
                _workspaces.Open(id);   // 新建后直接打开工作台
            }
        }
        catch (Exception ex) { StatusText.Text = "创建失败：" + ex.Message; }
        finally { IsEnabled = true; }
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        if (ProblemList.SelectedItem is ProblemInfo p)
            _workspaces.Open(p.Id);
    }

    // ---------- 比赛 ----------
    private async Task RefreshContests()
    {
        var items = await _wb.Contests.ListAsync();
        if (!IsLoaded) return;
        ContestList.ItemsSource = null;
        ContestList.ItemsSource = items;
        ContestList.DisplayMemberPath = nameof(ContestInfo.Display);
    }

    private async void OnContestSave(object sender, RoutedEventArgs e)
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
        var ids = ContestProblemsBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(s => int.TryParse(s, out _)).Select(int.Parse).ToArray();
        var draft = new ContestDraft(cid, name, ContestStartBox.Text.Trim(), ContestEndBox.Text.Trim(), ids);
        IsEnabled = false;
        try
        {
            var r = await _wb.Contests.SaveAsync(draft);
            ContestDetailBox.Text = r.Message;
            if (r.Ok) await RefreshContests();
            else MessageBox.Show(r.Message);
        }
        finally { IsEnabled = true; }
    }

    private async void OnContestPublish(object sender, RoutedEventArgs e)
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
        IsEnabled = false;
        StatusText.Text = $"正在发布比赛 C{c.Id} …";
        try
        {
            var r = await _wb.Contests.PublishAsync(c.Id, target);
            ContestDetailBox.Text = r.Message;
            StatusText.Text = r.Ok ? $"比赛 C{c.Id} 已发布" : r.Message;
        }
        finally { IsEnabled = true; }
    }

    private async void OnSelectContest(object sender, SelectionChangedEventArgs e)
    {
        if (ContestList.SelectedItem is not ContestInfo c) return;
        var text = await _wb.Contests.GetDetailAsync(c.Id);
        if (IsLoaded && text is not null) ContestDetailBox.Text = text;
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
