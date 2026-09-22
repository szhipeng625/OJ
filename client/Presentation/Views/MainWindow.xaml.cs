using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using client.Business.Services;
using client.DataAccess.Models;
using client.Presentation.Helpers;
using Microsoft.Win32;

namespace client.Presentation.Views;

public partial class MainWindow : Window
{
    private readonly JudgeService _judge;
    private readonly AuthService _auth;
    private readonly LocalIdentityService _identity;

    private string _nickname = "anonymous";
    private LoginResult? _me;     // MySQL 登录态；null = 本地模式

    // 题库列表行：左侧列表可搜索过滤，右侧个人信息复用同一数据源
    private readonly ObservableCollection<ProblemRow> _problemRows = new();
    private readonly ListCollectionView _problemView;
    private readonly ListCollectionView _profileView;

    // 比赛
    private List<ContestInfo> _contests = new();
    private ContestDetail? _currentContest;      // 列表页选中的比赛
    private ContestDetail? _roomContest;         // 已进入工作台的比赛
    private bool _roomVirtual;
    private Problem? _currentContestProblem;
    private readonly ObservableCollection<ProblemRow> _contestProblemRows = new();
    private readonly ListCollectionView _contestProblemView;

    // 做题窗口：同一时刻只保留一个，返回即关闭并释放内存
    private SolveWindow? _solveWindow;

    // 比赛开始倒计时刷新
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow(JudgeService judge, AuthService auth, LocalIdentityService identity)
    {
        InitializeComponent();
        _judge = judge;
        _auth = auth;
        _identity = identity;

        _problemView = new ListCollectionView(_problemRows);
        _profileView = new ListCollectionView(_problemRows);
        _contestProblemView = new ListCollectionView(_contestProblemRows);

        ProblemList.ItemsSource = _problemView;
        ProfileStatusList.ItemsSource = _profileView;
        ContestProblemList.ItemsSource = _contestProblemView;

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
        RefreshProfile();
        _countdownTimer.Tick += (_, _) => UpdateCountdown();
        _countdownTimer.Start();
        await LoadProblems();
        await LoadContests();
    }

    private void RefreshProfile()
    {
        if (_me is { } me)
        {
            string display = string.IsNullOrWhiteSpace(me.Nickname) ? me.Username : me.Nickname;
            ProfileName.Text = display;
            ProfileRole.Text = $"{me.Role} · {me.Username}";
            EditNickBtn.Visibility = Visibility.Visible;
            UploadAvatarBtn.Visibility = Visibility.Visible;
        }
        else
        {
            ProfileName.Text = _nickname;
            ProfileRole.Text = "本地用户";
            EditNickBtn.Visibility = Visibility.Collapsed;
            UploadAvatarBtn.Visibility = Visibility.Collapsed;
        }
        ApplyAvatar(_me?.Avatar);
    }

    private void ApplyAvatar(string? avatar)
    {
        if (string.IsNullOrWhiteSpace(avatar))
        {
            AvatarImage.Source = null;
            AvatarImage.Visibility = Visibility.Collapsed;
            AvatarFallback.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            string base64 = avatar.Contains(',') ? avatar[(avatar.IndexOf(',') + 1)..] : avatar;
            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            AvatarImage.Source = bmp;
            AvatarImage.Visibility = Visibility.Visible;
            AvatarFallback.Visibility = Visibility.Collapsed;
        }
        catch
        {
            AvatarImage.Source = null;
            AvatarImage.Visibility = Visibility.Collapsed;
            AvatarFallback.Visibility = Visibility.Visible;
        }
    }

    private async void OnEditNickname(object sender, RoutedEventArgs e)
    {
        if (_me is null) return;
        string? newNick = PromptText("修改昵称", "请输入新昵称：",
            string.IsNullOrWhiteSpace(_me.Nickname) ? _me.Username : _me.Nickname);
        if (string.IsNullOrWhiteSpace(newNick)) return;
        bool ok = await _auth.UpdateProfileAsync(_me.Token, newNick.Trim(), _me.Avatar);
        if (!ok) { MessageBox.Show("修改昵称失败"); return; }
        _me = _me with { Nickname = newNick.Trim() };
        RefreshProfile();
        NickBadge.Text = "👤 " + (string.IsNullOrWhiteSpace(_me.Nickname) ? _me.Username : _me.Nickname);
    }

    private async void OnUploadAvatar(object sender, RoutedEventArgs e)
    {
        if (_me is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "选择头像图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            byte[] bytes = File.ReadAllBytes(dlg.FileName);
            string ext = (Path.GetExtension(dlg.FileName) ?? ".png").ToLowerInvariant();
            string mime = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "image/png"
            };
            string dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            bool ok = await _auth.UpdateProfileAsync(_me.Token, _me.Nickname, dataUrl);
            if (!ok) { MessageBox.Show("上传头像失败"); return; }
            _me = _me with { Avatar = dataUrl };
            ApplyAvatar(dataUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show("读取图片失败：" + ex.Message);
        }
    }

    private string? PromptText(string title, string message, string initial)
    {
        var win = new Window
        {
            Title = title,
            Width = 380,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { Height = 26, Text = initial };
        panel.Children.Add(box);
        var btn = new Button { Content = "确定", Width = 80, Height = 28, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        btn.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(btn);
        win.Content = panel;
        return win.ShowDialog() == true ? box.Text : null;
    }

    private void UpdateCountdown()
    {
        if (_currentContest is { } c)
        {
            string cd = ContestPolicy.Countdown(c.StartTime);
            CountdownText.Text = string.IsNullOrEmpty(cd) ? "" : "距比赛开始还有 " + cd;
        }
        else
        {
            CountdownText.Text = "";
        }
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
            var progress = await _judge.GetUserProgressAsync(_nickname) ?? new();
            var acSet = progress.Where(p => p.Ac).Select(p => p.ProblemId).ToHashSet();
            var verdictMap = progress.ToDictionary(p => p.ProblemId, p => p.Verdict);

            _problemRows.Clear();
            if (problems is { Count: > 0 })
            {
                foreach (var p in problems)
                    _problemRows.Add(MakeRow(p, acSet, verdictMap));

                ConnBadge.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x99, 0x66));
                ConnBadge.Text = $"已连接 · {problems.Count} 题";
            }
            else
            {
                ConnBadge.Background = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                ConnBadge.Text = "无题目";
            }
            RefreshProblemView();
            UpdateProfile();
        }
        catch (Exception)
        {
            ConnBadge.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x53, 0x4F));
            ConnBadge.Text = "连接失败";
        }
    }

    private static ProblemRow MakeRow(Problem p, HashSet<int> acSet, Dictionary<int, string> verdictMap)
    {
        string tags = p.Tags is { Length: > 0 } ? $"  [{string.Join(",", p.Tags)}]" : "";
        string display = $"[{p.Id}] {p.Title}{tags}";
        if (acSet.Contains(p.Id))
            return new ProblemRow(p, display, "✔", GreenBrush, "已通过");
        if (verdictMap.TryGetValue(p.Id, out var v))
            return new ProblemRow(p, display, "✘", RedBrush, $"未通过（最近提交：{v}）");
        return new ProblemRow(p, display, "", GrayBrush, "未提交");
    }

    private void RefreshProblemView()
    {
        string q = SearchBox?.Text?.Trim() ?? "";
        _problemView.Filter = o => o is ProblemRow r &&
            (string.IsNullOrEmpty(q) ||
             r.Problem.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase) ||
             r.Problem.Title.Contains(q, StringComparison.OrdinalIgnoreCase));
        _problemView.Refresh();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => RefreshProblemView();

    private void UpdateProfile()
    {
        int total = _problemRows.Count;
        int solved = _problemRows.Count(r => r.StatusMark == "✔");
        StatSolved.Text = solved.ToString();
        StatTotal.Text = total.ToString();
        StatRatio.Text = total > 0 ? $"{solved * 100 / total}%" : "0%";
        SolveProgress.Maximum = total > 0 ? total : 1;
        SolveProgress.Value = solved;
    }

    private void ProblemList_OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProblemList.SelectedItem is ProblemRow row)
            ShowProblemDescription(row.Problem);
    }

    private void ProblemList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProblemList.SelectedItem is ProblemRow row)
            OpenSolveWindow(row.Problem, null, false);
    }

    private void ShowProblemDescription(Problem p)
    {
        HomeDescTitle.Text = p.Title;
        MarkdownRenderer.Render(HomeDescBox, MarkdownRenderer.CombineStatement(p.Description, p.SampleIn, p.SampleOut));
        string tags = p.Tags is { Length: > 0 } ? string.Join(", ", p.Tags) : "无";
        HomeMetaText.Text = $"时间限制 {p.TimeLimitMs}ms · 内存 {p.MemLimitMB}MB · 标签：{tags}";
        ProfilePanel.Visibility = Visibility.Collapsed;
        ProblemDescPanel.Visibility = Visibility.Visible;
    }

    private void OnBackToProfile(object sender, RoutedEventArgs e)
    {
        ProblemDescPanel.Visibility = Visibility.Collapsed;
        ProfilePanel.Visibility = Visibility.Visible;
    }

    private void OpenSolveWindow(Problem p, ContestDetail? contest, bool roomVirtual)
    {
        _solveWindow?.Close();
        _solveWindow = null;
        var win = new SolveWindow(_judge, p, _nickname, contest, roomVirtual) { Owner = this };
        _solveWindow = win;
        win.Closed += async (_, _) =>
        {
            if (ReferenceEquals(_solveWindow, win)) _solveWindow = null;
            await RefreshProblemStatusAsync();
        };
        win.Show();
    }

    /// <summary>做题窗口返回后刷新题库 / 比赛的通过状态与个人信息统计。</summary>
    private async Task RefreshProblemStatusAsync()
    {
        try
        {
            var progress = await _judge.GetUserProgressAsync(_nickname) ?? new();
            var acSet = progress.Where(p => p.Ac).Select(p => p.ProblemId).ToHashSet();
            var verdictMap = progress.ToDictionary(p => p.ProblemId, p => p.Verdict);
            for (int i = 0; i < _problemRows.Count; i++)
            {
                var old = _problemRows[i];
                _problemRows[i] = MakeRow(old.Problem, acSet, verdictMap);
            }
            UpdateProfile();

            if (_roomContest is not null)
            {
                var cProgress = await _judge.GetUserContestProgressAsync(_roomContest.Id, _nickname) ?? new();
                var cAcSet = cProgress.Where(x => x.Ac).Select(x => x.ProblemId).ToHashSet();
                var cVerdictMap = cProgress.ToDictionary(x => x.ProblemId, x => x.Verdict);
                for (int i = 0; i < _contestProblemRows.Count; i++)
                {
                    var old = _contestProblemRows[i];
                    _contestProblemRows[i] = MakeRow(old.Problem, cAcSet, cVerdictMap);
                }
            }
        }
        catch { /* 状态刷新失败不影响主流程 */ }
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
                RegisterHint.Text = ended ? "比赛已结束，可查看比赛（报名后虚拟补赛）" : "报名后即可进入比赛";
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
            var probs = _judge.FilterContestProblems(all, _roomContest.Problems);
            var progress = await _judge.GetUserContestProgressAsync(_roomContest.Id, _nickname) ?? new();
            var acSet = progress.Where(x => x.Ac).Select(x => x.ProblemId).ToHashSet();
            var verdictMap = progress.ToDictionary(x => x.ProblemId, x => x.Verdict);

            _contestProblemRows.Clear();
            foreach (var p in probs)
                _contestProblemRows.Add(MakeRow(p, acSet, verdictMap));

            string status = ContestPolicy.Status(_roomContest.StartTime, _roomContest.EndTime);
            RoomTitle.Text = $"【C{_roomContest.Id}】{_roomContest.Name}（{(_roomVirtual ? "虚拟参赛" : "正式参赛")}）";
            ContestInfoText.Text = $"{_roomContest.StartTime} ~ {_roomContest.EndTime}  ·  {status}  ·  你正在以{(_roomVirtual ? "虚拟" : "正式")}身份参赛";

            _currentContestProblem = null;
            ContestProblemList.SelectedIndex = -1;
            ShowContestInfo();

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

    // 返回比赛列表：关闭做题窗口与工作台并清空状态
    private void OnExitContest(object sender, RoutedEventArgs e)
    {
        _solveWindow?.Close();
        _solveWindow = null;
        _roomContest = null;
        _currentContestProblem = null;
        _contestProblemRows.Clear();
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
        if (ContestProblemList.SelectedItem is ProblemRow row)
        {
            _currentContestProblem = row.Problem;
            ShowContestProblemDescription(row.Problem);
        }
    }

    private void ContestProblemList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_roomContest is null) return;
        if (ContestProblemList.SelectedItem is ProblemRow row)
            OpenSolveWindow(row.Problem, _roomContest, _roomVirtual);
    }

    private void ShowContestProblemDescription(Problem p)
    {
        ContestDescTitle.Text = p.Title;
        MarkdownRenderer.Render(ContestDescBox, MarkdownRenderer.CombineStatement(p.Description, p.SampleIn, p.SampleOut));
        string tags = p.Tags is { Length: > 0 } ? string.Join(", ", p.Tags) : "无";
        ContestMetaText.Text = $"时间限制 {p.TimeLimitMs}ms · 内存 {p.MemLimitMB}MB · 标签：{tags}";
        ContestInfoPanel.Visibility = Visibility.Collapsed;
        ContestProblemDescPanel.Visibility = Visibility.Visible;
    }

    private void ShowContestInfo()
    {
        ContestProblemDescPanel.Visibility = Visibility.Collapsed;
        ContestInfoPanel.Visibility = Visibility.Visible;
    }

    private void OnBackToContestInfo(object sender, RoutedEventArgs e) => ShowContestInfo();

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
            bool viewAll = _me?.Role == "admin" || ContestPolicy.IsEnded(_roomContest.EndTime);
            var subs = await _judge.GetContestSubmissionsAsync(_roomContest.Id, _nickname, viewAll);
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
            SubsHeaderText.Text = viewAll
                ? "本场提交记录（每人每题保留最后一次提交结果，按时间倒序）"
                : "本场提交记录（比赛进行中，仅显示你自己的提交）";
        }
        catch { }
    }

    // 题目列表行（左侧列表与右侧个人信息列表共用）
    public sealed record ProblemRow(
        Problem Problem,
        string Display,
        string StatusMark,
        Brush StatusBrush,
        string StatusTip);

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xFF, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush GreenBrush = FrozenBrush(0x33, 0x99, 0x66);
    private static readonly Brush RedBrush = FrozenBrush(0xD9, 0x53, 0x4F);
    private static readonly Brush GrayBrush = FrozenBrush(0x9E, 0x9E, 0x9E);
}
