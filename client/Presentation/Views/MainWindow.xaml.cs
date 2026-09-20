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
                        ToolTip = $"时间限制 {p.TimeLimitMs}ms · 内存 {p.MemLimitMB}MB · 版本 v{p.Version}"
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
        MetaText.Text = $"时间限制 {p.TimeLimitMs}ms · 内存 {p.MemLimitMB}MB · 版本 v{p.Version} · 标签：{tags}";
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
            var tab = ContestTab;
            tab.IsEnabled = true;
        }
        catch { /* 无比赛也正常 */ }
    }

    private async void OnSelectContest(object sender, SelectionChangedEventArgs e)
    {
        if (ContestList.SelectedItem is not ListBoxItem { Tag: ContestInfo c }) return;
        try
        {
            _currentContest = await _judge.GetContestAsync(c.Id);
            if (_currentContest is null) return;

            string status = ContestPolicy.Status(_currentContest.StartTime, _currentContest.EndTime);
            ContestInfoText.Text = $"【C{_currentContest.Id}】{_currentContest.Name}  ·  {_currentContest.StartTime} ~ {_currentContest.EndTime}  ·  {status}";

            // 过滤题目
            var all = await _judge.GetProblemsAsync() ?? new();
            _contestProblems = _judge.FilterContestProblems(all, _currentContest.Problems);

            ContestProblemList.Items.Clear();
            foreach (var p in _contestProblems)
                ContestProblemList.Items.Add(new ListBoxItem { Content = $"[{p.Id}] {p.Title}", Tag = p });

            // 已结束的比赛默认勾选虚拟参赛
            VirtualBox.IsChecked = ContestPolicy.IsEnded(_currentContest.EndTime);

            await RefreshBoard();
        }
        catch (Exception ex)
        {
            ContestStatusText.Text = "加载比赛失败：" + ex.Message;
        }
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
        if (_currentContest is null) { MessageBox.Show("请先在左侧选择比赛"); return; }
        if (_currentContestProblem is null) { MessageBox.Show("请选择一道比赛题目"); return; }

        var code = ContestCodeBox.Text;
        if (string.IsNullOrWhiteSpace(code)) { MessageBox.Show("请填写代码"); return; }

        bool virt = VirtualBox.IsChecked == true;
        // 时间窗口校验（业务规则在 BLL ContestPolicy）
        if (!ContestPolicy.CanSubmitOfficial(_currentContest.StartTime, _currentContest.EndTime, virt, out var reason))
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
            var result = await _judge.SubmitAsync(_currentContestProblem.Id, code, _nickname, virt);
            if (result is null) { ContestVerdictText.Text = "结果：无响应"; return; }
            ContestVerdictText.Text = $"结果：{result.Verdict}   —   {result.Detail}";
            ContestVerdictText.Foreground = result.Verdict == "AC"
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x99, 0x66))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x53, 0x4F));
            foreach (var c in result.Cases) _contestCases.Add(c);
            ContestStatusText.Text = $"提交 #{result.Id}（{(virt ? "虚拟" : "正式")}）完成";
            await RefreshBoard();
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
        if (_currentContest is null) return;
        try
        {
            var board = await _judge.GetBoardAsync(_currentContest.Id);
            BoardListOfficial.Items.Clear();
            BoardListVirtual.Items.Clear();
            if (board is null) return;
            foreach (var r in board.Official)
                BoardListOfficial.Items.Add($"#{r.Rank}  {r.Username}   AC {r.Solved} 题 · 罚时 {r.Penalty} 分");
            foreach (var r in board.Virtual)
                BoardListVirtual.Items.Add($"#{r.Rank}  {r.Username}   AC {r.Solved} 题 · 罚时 {r.Penalty} 分");
        }
        catch { }
    }
}
