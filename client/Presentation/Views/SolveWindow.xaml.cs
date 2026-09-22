using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using client.Business.Services;
using client.DataAccess.Models;
using client.Presentation.Helpers;

namespace client.Presentation.Views;

/// <summary>
/// 独立做题窗口：左题面、右代码。每次进入都新建，关闭即释放内存；
/// 打开时从存储中取回该题（该比赛，0=练习）最近一次提交的代码载入编辑器。
/// </summary>
public partial class SolveWindow : Window
{
    private readonly JudgeService _judge;
    private readonly Problem _problem;
    private readonly string _username;
    private readonly ContestDetail? _contest;   // null = 练习模式
    private readonly bool _roomVirtual;

    private readonly ObservableCollection<CaseResult> _cases = new();
    private bool _released;

    public SolveWindow(JudgeService judge, Problem problem, string username,
                       ContestDetail? contest, bool roomVirtual)
    {
        InitializeComponent();
        _judge = judge;
        _problem = problem;
        _username = username;
        _contest = contest;
        _roomVirtual = roomVirtual;

        CaseList.ItemsSource = _cases;

        if (contest is null)
        {
            Title = $"P{problem.Id} {problem.Title} - 做题";
            TitleText.Text = $"P{problem.Id} {problem.Title}";
        }
        else
        {
            string kind = roomVirtual ? "虚拟参赛" : "正式参赛";
            Title = $"C{contest.Id} · P{problem.Id} {problem.Title} - 做题";
            TitleText.Text = $"C{contest.Id} · P{problem.Id} {problem.Title}（{kind}）";
        }

        string tags = problem.Tags is { Length: > 0 } ? string.Join(", ", problem.Tags) : "无";
        DescTitle.Text = problem.Title;
        MetaText.Text = $"时间限制 {problem.TimeLimitMs}ms · 内存 {problem.MemLimitMB}MB · 标签：{tags}";
        MarkdownRenderer.Render(DescBox, MarkdownRenderer.CombineStatement(problem.Description, problem.SampleIn, problem.SampleOut));

        Loaded += async (_, _) => await LoadSavedCodeAsync();
        Closed += (_, _) => ReleaseResources();
    }

    private async Task LoadSavedCodeAsync()
    {
        const string DefaultCode =
            "#include <iostream>\nusing namespace std;\nint main(){\n    return 0;\n}";
        try
        {
            int contestId = _contest?.Id ?? 0;
            var sol = await _judge.GetUserSolutionAsync(_problem.Id, _username, contestId);
            if (sol is { Found: true } && !string.IsNullOrEmpty(sol.Code))
            {
                CodeBox.Text = sol.Code;
                LastVerdictText.Text = $"上次提交：{sol.Verdict} · {sol.Ts}";
                StatusText.Text = "已载入上次保存的代码";
            }
            else
            {
                CodeBox.Text = DefaultCode;
                StatusText.Text = "暂无历史代码";
            }
        }
        catch
        {
            CodeBox.Text = DefaultCode;
            StatusText.Text = "载入历史代码失败";
        }
    }

    private async void SubmitBtn_OnClick(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text;
        if (string.IsNullOrWhiteSpace(code)) { MessageBox.Show("请填写代码"); return; }

        SubmitBtn.IsEnabled = false;
        StatusText.Text = "判题中...";
        VerdictText.Text = "结果：判题中...";
        VerdictText.Foreground = Brushes.Gray;
        _cases.Clear();

        try
        {
            SubmitResult? result;
            if (_contest is null)
            {
                result = await _judge.SubmitAsync(_problem.Id, code, _username, false);
            }
            else
            {
                if (!ContestPolicy.CanSubmitOfficial(_contest.StartTime, _contest.EndTime, _roomVirtual, out var reason))
                {
                    MessageBox.Show(reason);
                    StatusText.Text = "";
                    VerdictText.Text = "结果：等待提交";
                    return;
                }
                result = await _judge.SubmitContestAsync(_problem.Id, code, _username, _roomVirtual, _contest.Id);
            }

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

    private void OnBack(object sender, RoutedEventArgs e) => Close();

    /// <summary>窗口关闭时释放 WebBrowser / 代码编辑器 / 结果集合等资源。</summary>
    private void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        try { DescBox.NavigateToString("about:blank"); } catch { }
        try { DescBox.Dispose(); } catch { }
        try { CodeBox.Text = ""; } catch { }
        _cases.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
