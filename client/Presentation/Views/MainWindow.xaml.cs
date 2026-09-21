using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using client.Business.Services;
using client.DataAccess.Models;
using client.Presentation.Helpers;

namespace client.Presentation.Views;

public partial class MainWindow : Window
{
    private readonly JudgeService _judge;
    private readonly AuthService _auth;
    private readonly LocalIdentityService _identity;

    private readonly ObservableCollection<CaseResult> _cases = new();
    private readonly ObservableCollection<CaseResult> _contestCases = new();
    private Problem? _current;
    private string _nickname = "anonymous";
    private LoginResult? _me;     // MySQL 登录态；null = 本地模式

    private List<ContestInfo> _contests = new();
    private ContestDetail? _currentContest;
    private List<Problem> _contestProblems = new();
    private Problem? _currentContestProblem;

    public MainWindow(JudgeService judge, AuthService auth, LocalIdentityService identity)
    {
        InitializeComponent();
        _judge = judge;
        _auth = auth;
        _identity = identity;
        CaseList.ItemsSource = _cases;
        ContestCaseList.ItemsSource = _contestCases;
        Loaded += async (_, _) => await OnLoaded();
    }

    private async Task OnLoaded()
    {
        _me = _auth.CurrentUser;
        if (_me is { } me)
        {
            _nickname = me.Username;
            NickBadge.Text = "👤 " + me.Username + " [" + me.Role + "]";
        }
        else
        {
            LoadNickname();
        }
        await LoadProblems();
        await LoadContests();
    }

    private void LoadNickname()
    {
        if (_identity.Exists)
        {
            _nickname = _identity.Load();
        }
        else
        {
            _nickname = PromptNickname();
            _identity.Save(_nickname);
        }
        NickBadge.Text = "👤 " + _nickname;
    }

    private string PromptNickname()
    {
        var win = new Window
        {
            Title = "设置昵称",
            Width = 360,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "请输入你的昵称（用于榜单显示）：", Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { Height = 26 };
        panel.Children.Add(box);
        var btn = new Button { Content = "确定", Width = 80, Height = 28, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        btn.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(btn);
        win.Content = panel;
        if (win.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text))
            return box.Text.Trim();
        return "anonymous";
    }


    // ---------- 题库 ----------
    private async Task LoadProblems()
    {
        ConnBadge.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xE6, 0x7E));
        ConnBadge.Text = "连接中...";
        try
        {
            var problems = await _judge.GetProblemsAsync();
            ProblemList.Items.Clear();
            if (problems is { Count: > 0 })
            {
                foreach (var p in problems)
                {
                    string tags = p.Tags is { Length: > 0 } ? $"  [{string.Join(",", p.Tags)}]" : "";
                    ProblemList.Items.Add(new ListBoxItem
                    {
                        Content = $"[{p.Id}] {p.Title}{tags}",
                        Tag = p,
                        ToolTip = $"时间限制 {p.TimeLimitMs}ms · 内存 {p.MemLimitMB}MB"
                    });
                }
                ProblemList.SelectedIndex = 0;
                ConnBadge.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x99, 0x66));
                ConnBadge.Text = $"已连接 · {problems.Count} 题";
            }
            else
            {
                ConnBadge.Background = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                ConnBadge.Text = "无题目";
            }
        }
        catch (Exception ex)
        {
            ConnBadge.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x53, 0x4F));
            ConnBadge.Text = "连接失败";
            StatusText.Text = "无法连接后端：" + ex.Message;
        }
    }

    private void ProblemList_OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProblemList.SelectedItem is ListBoxItem { Tag: Problem p })
        {
            _current = p;
            ShowProblem(p);
        }
    }

    private void ShowProblem(Problem p)
    {
        MarkdownRenderer.Render(DescBox, p.Description);
        string tags = p.Tags is { Length: > 0 } ? string.Join(", ", p.Tags) : "无";
        MetaText.Text = $"时间限制 {p.TimeLimitMs}ms · 内存 {p.MemLimitMB}MB · 标签：{tags}";
    }

    private async void SubmitBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) { MessageBox.Show("请先在左侧选择题目"); return; }
        var code = CodeBox.Text;
        if (string.IsNullOrWhiteSpace(code)) { MessageBox.Show("请填写代码"); return; }

        SubmitBtn.IsEnabled = false;
        StatusText.Text = "判题中...";
        VerdictText.Text = "结果：判题中...";
        VerdictText.Foreground = Brushes.Gray;
        _cases.Clear();

        try
        {
            var result = await _judge.SubmitAsync(_current.Id, code, _nickname, false);
            if (result is null) { VerdictText.Text = "结果：无响应"; return; }
            VerdictText.Text = $"结果：{result.Verdict}   —   {result.Detail}";
            VerdictText.Foreground = result.Verdict == "AC"
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x99, 0x66))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x53, 0x4F));
            foreach (var c in result.Cases) _cases.Add(c);
            StatusText.Text = $"提交 #{result.Id} 完成";
        }
        catch (Exception ex)
        {
            VerdictText.Text = "结果：请求失败";
            StatusText.Text = ex.Message;
        }
        finally { SubmitBtn.IsEnabled = true; }
    }

    // ---------- 比赛 ----------
    // 视图状态：列表页选中的比赛 / 已进入工作台的比赛及其参赛身份
    private ContestDetail? _roomContest;
    private bool _roomVirtual;

    private async Task LoadContests()
    {
        try
        {
            _contests = await _judge.GetContestsAsync() ?? new();
            ContestList.Items.Clear();
            foreach (var c in _contests)
            {
                string status = ContestPolicy.Status(c.StartTime, c.EndTime);
                ContestList.Items.Add(new ListBoxItem
                {
                    Content = $"[C{c.Id}] {c.Name}（{c.ProblemCount} 题 · {status}）",
                    Tag = c,
                    ToolTip = $"{c.StartTime} ~ {c.EndTime}"
                });
            }
            ContestTab.IsEnabled = true;
        }
        catch { /* 无比赛也正常 */ }
    }

    private async void OnRefreshContests(object sender, RoutedEventArgs e)
    {
        await LoadContests();
    }

    // 列表页选中比赛：只展示比赛信息与报名入口，不加载题目
    private async void OnSelectContest(object sender, SelectionChangedEventArgs e)
    {
        if (ContestList.SelectedItem is not ListBoxItem { Tag: ContestInfo c }) return;
        try
        {
            _currentContest = await _judge.GetContestAsync(c.Id);
            if (_currentContest is null) return;

            string status = ContestPolicy.Status(_currentContest.StartTime, _currentContest.EndTime);
            ContestListTitle.Text = $"【C{_currentContest.Id}】{_currentContest.Name}";
            ContestListMeta.Text = $"{_currentContest.StartTime} ~ {_currentContest.EndTime}  ·  {status}  ·  共 {_currentContest.Problems.Length} 题";
            MarkdownRenderer.Render(ContestListDesc, _currentContest.Description);

            bool ended = ContestPolicy.IsEnded(_currentContest.EndTime);
            RegisterVirtualBox.IsChecked = ended;
            RegisterVirtualBox.IsEnabled = false;   // 参赛身份由比赛时间窗决定，报名时锁定

            // 查询报名状态
            var reg = await _judge.GetContestRegistrationAsync(_currentContest.Id, _nickname);
            if (reg.Registered)
            {
                RegisterBtn.Visibility = Visibility.Collapsed;
                EnterContestBtn.Visibility = Visibility.Visible;
                RegisterVirtualBox.IsChecked = reg.Virtual;
                RegisterHint.Text = reg.Virtual ? "已报名：虚拟参赛" : "已报名：正式参赛";
            }
            else
            {
                RegisterBtn.Visibility = Visibility.Visible;
                EnterContestBtn.Visibility = Visibility.Collapsed;
                RegisterHint.Text = ended ? "比赛已结束，只能虚拟参赛" : "报名后即可进入比赛";
            }
            RegisterBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            RegisterHint.Text = "加载比赛失败：" + ex.Message;
        }
    }

    private async void OnRegisterContest(object sender, RoutedEventArgs e)
    {
        if (_currentContest is null) return;
        try
        {
            bool virt = RegisterVirtualBox.IsChecked == true;
            if (!ContestPolicy.CanSubmitOfficial(_currentContest.StartTime, _currentContest.EndTime, virt, out var reason))
            {
                // 未开始/进行中要求正式参赛；结束后只能虚拟
                MessageBox.Show(reason);
                return;
            }
            RegisterBtn.IsEnabled = false;
            RegisterHint.Text = "报名中...";
            var reg = await _judge.RegisterContestAsync(_currentContest.Id, _nickname, virt);
            if (!reg.Ok)
            {
                RegisterHint.Text = "报名失败，请重试";
                RegisterBtn.IsEnabled = true;
                return;
            }
            RegisterBtn.Visibility = Visibility.Collapsed;
            EnterContestBtn.Visibility = Visibility.Visible;
            _roomVirtual = reg.Virtual;
            RegisterHint.Text = reg.Virtual ? "已报名：虚拟参赛" : "已报名：正式参赛";
        }
        catch (Exception ex)
        {
            RegisterHint.Text = "报名失败：" + ex.Message;
            RegisterBtn.IsEnabled = true;
        }
    }

    // 报名后进入工作台：此时才加载本场题目
    private async void OnEnterContest(object sender, RoutedEventArgs e)
    {
        if (_currentContest is null) return;
        try
        {
            var reg = await _judge.GetContestRegistrationAsync(_currentContest.Id, _nickname);
            if (!reg.Registered)
            {
                MessageBox.Show("请先报名再进入比赛");
                return;
            }
            _roomVirtual = reg.Virtual;
            _roomContest = _currentContest;

            var all = await _judge.GetProblemsAsync() ?? new();
            _contestProblems = _judge.FilterContestProblems(all, _roomContest.Problems);
            ContestProblemList.Items.Clear();
            foreach (var p in _contestProblems)
                ContestProblemList.Items.Add(new ListBoxItem { Content = $"[{p.Id}] {p.Title}", Tag = p });

            string status = ContestPolicy.Status(_roomContest.StartTime, _roomContest.EndTime);
            RoomTitle.Text = $"【C{_roomContest.Id}】{_roomContest.Name}（{(_roomVirtual ? "虚拟参赛" : "正式参赛")}）";
            ContestInfoText.Text = $"{_roomContest.StartTime} ~ {_roomContest.EndTime}  ·  {status}  ·  你正在以{(_roomVirtual ? "虚拟" : "正式")}身份参赛";

            // 重置做题区
            ContestCodeBox.Text = "#include <iostream>\nusing namespace std;\nint main(){\n    return 0;\n}";
            _contestCases.Clear();
            ContestVerdictText.Text = "结果：等待提交";
            ContestVerdictText.Foreground = Brushes.Gray;
            ContestStatusText.Text = "";
            ContestMetaText.Text = "";
            MarkdownRenderer.Render(ContestDescBox, "");
            _currentContestProblem = null;
            ContestProblemList.SelectedIndex = -1;

            // 切换到工作台，默认做题页
            ContestListView.Visibility = Visibility.Collapsed;
            ContestRoomView.Visibility = Visibility.Visible;
            ShowSolve();
        }
        catch (Exception ex)
        {
            MessageBox.Show("进入比赛失败：" + ex.Message);
        }
    }

    // 返回比赛列表：关闭工作台并清空题目/榜单/提交记录状态
    private void OnExitContest(object sender, RoutedEventArgs e)
    {
        _roomContest = null;
        _currentContestProblem = null;
        _contestProblems = new();
        ContestProblemList.Items.Clear();
        _contestCases.Clear();
        BoardListView.ItemsSource = null;
        SubmissionListView.ItemsSource = null;
        ContestRoomView.Visibility = Visibility.Collapsed;
        ContestListView.Visibility = Visibility.Visible;
    }

    private void OnNavSolve(object sender, RoutedEventArgs e) => ShowSolve();

    private async void OnNavBoard(object sender, RoutedEventArgs e)
    {
        RoomSolvePanel.Visibility = Visibility.Collapsed;
        RoomSubsPanel.Visibility = Visibility.Collapsed;
        SubmissionListView.ItemsSource = null;   // 离开提交记录页即关闭其内容
        RoomBoardPanel.Visibility = Visibility.Visible;
        await RefreshBoard();
    }

    private async void OnNavSubmissions(object sender, RoutedEventArgs e)
    {
        RoomSolvePanel.Visibility = Visibility.Collapsed;
        RoomBoardPanel.Visibility = Visibility.Collapsed;
        BoardListView.ItemsSource = null;       // 离开排行榜页即关闭其内容
        RoomSubsPanel.Visibility = Visibility.Visible;
        await RefreshSubmissions();
    }

    private void ShowSolve()
    {
        RoomBoardPanel.Visibility = Visibility.Collapsed;
        RoomSubsPanel.Visibility = Visibility.Collapsed;
        // 返回做题页时关闭榜单/提交记录界面，不保留其内容
        BoardListView.ItemsSource = null;
        SubmissionListView.ItemsSource = null;
        RoomSolvePanel.Visibility = Visibility.Visible;
    }

    private void OnSelectContestProblem(object sender, SelectionChangedEventArgs e)
    {
        if (ContestProblemList.SelectedItem is not ListBoxItem { Tag: Problem p }) return;
        _currentContestProblem = p;
        MarkdownRenderer.Render(ContestDescBox, p.Description);
        string tags = p.Tags is { Length: > 0 } ? string.Join(", ", p.Tags) : "无";
        ContestMetaText.Text = $"时间限制 {p.TimeLimitMs}ms · 内存 {p.MemLimitMB}MB · 标签：{tags}";
    }

    private async void ContestSubmit_OnClick(object sender, RoutedEventArgs e)
    {
        if (_roomContest is null) { MessageBox.Show("请先进入比赛"); return; }
        if (_currentContestProblem is null) { MessageBox.Show("请选择一道比赛题目"); return; }

        var code = ContestCodeBox.Text;
        if (string.IsNullOrWhiteSpace(code)) { MessageBox.Show("请填写代码"); return; }

        // 时间窗口校验（业务规则在 BLL ContestPolicy；参赛身份报名时锁定）
        if (!ContestPolicy.CanSubmitOfficial(_roomContest.StartTime, _roomContest.EndTime, _roomVirtual, out var reason))
        {
            MessageBox.Show(reason);
            return;
        }

        ContestSubmitBtn.IsEnabled = false;
        ContestStatusText.Text = "判题中...";
        ContestVerdictText.Text = "结果：判题中...";
        ContestVerdictText.Foreground = Brushes.Gray;
        _contestCases.Clear();

        try
        {
            var result = await _judge.SubmitContestAsync(_currentContestProblem.Id, code,
                                                         _nickname, _roomVirtual, _roomContest.Id);
            if (result is null) { ContestVerdictText.Text = "结果：无响应"; return; }
            ContestVerdictText.Text = $"结果：{result.Verdict}   —   {result.Detail}";
            ContestVerdictText.Foreground = result.Verdict == "AC"
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x99, 0x66))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x53, 0x4F));
            foreach (var c in result.Cases) _contestCases.Add(c);
            ContestStatusText.Text = $"提交 #{result.Id}（{(_roomVirtual ? "虚拟" : "正式")}）完成";
        }
        catch (Exception ex)
        {
            ContestVerdictText.Text = "结果：请求失败";
            ContestStatusText.Text = ex.Message;
        }
        finally { ContestSubmitBtn.IsEnabled = true; }
    }

    private async void OnRefreshBoard(object sender, RoutedEventArgs e) => await RefreshBoard();

    private async Task RefreshBoard()
    {
        if (_roomContest is null) return;
        try
        {
            var board = await _judge.GetBoardAsync(_roomContest.Id);
            // 正式/虚拟合并为一张榜：虚拟行无名次（显示 —），名字带 *，位置按成绩排列
            BoardListView.ItemsSource = ContestPolicy.MergeBoard(board)
                .Select(e => new
                {
                    Rank = e.Virtual ? "—" : e.Rank.ToString(),
                    e.Username,
                    Solved = e.Solved,
                    Penalty = e.Penalty
                })
                .ToList();
        }
        catch { }
    }

    private async void OnRefreshSubmissions(object sender, RoutedEventArgs e) => await RefreshSubmissions();

    private async Task RefreshSubmissions()
    {
        if (_roomContest is null) return;
        try
        {
            var subs = await _judge.GetContestSubmissionsAsync(_roomContest.Id);
            SubmissionListView.ItemsSource = (subs ?? new())
                .Select(s => new
                {
                    s.Ts,
                    ProblemId = s.ProblemId,
                    Username = s.Virtual ? s.Username + " *" : s.Username,
                    s.Verdict,
                    s.Detail,
                    Kind = s.Virtual ? "虚拟" : "正式"
                })
                .ToList();
        }
        catch { }
    }
}
