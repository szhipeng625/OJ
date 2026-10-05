using System.Windows;
using System.Windows.Controls;
using author.Business.Services;

namespace author.Presentation.Views;

/// <summary>
/// 审计与用量查看：左边每次指令的账（token / 成本 / 缓存命中 / 工具次数），
/// 右边是该次运行改过的题面（改前 → 改后）。
///
/// 这个窗口存在的理由：Codex 能直接改写题库，出了问题必须能回溯
/// 「谁、按哪条指令、把题面从什么改成了什么」。
/// </summary>
public partial class CodexAuditWindow : Window
{
    private readonly MiddlewareCodexRecorder _recorder;
    private readonly int _problemId;

    public CodexAuditWindow(MiddlewareCodexRecorder recorder, int problemId)
    {
        InitializeComponent();
        _recorder = recorder;
        _problemId = problemId;
        ScopeText.Text = problemId > 0 ? $"仅 P{problemId}" : "全部题目";
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        StatusText.Text = "正在读取…";
        try
        {
            var runs = await _recorder.FetchRunsAsync(limit: 200, problemId: _problemId);
            RunsList.ItemsSource = runs;

            if (runs.Count == 0)
            {
                StatusText.Text = string.IsNullOrEmpty(_recorder.LastError)
                    ? "还没有 Codex 运行记录（用过一次助手之后这里会有数据）。"
                    : _recorder.LastError;
                AuditsList.ItemsSource = null;
                return;
            }

            StatusText.Text = $"共 {runs.Count} 次运行 · 合计 ${runs.Sum(r => r.CostUsd):0.0000} · "
                            + $"输出 {runs.Sum(r => r.OutputTokens):N0} tok · "
                            + $"缓存读 {runs.Sum(r => r.CacheReadTokens):N0} tok";
            RunsList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
    }

    private async void OnSelectRun(object sender, SelectionChangedEventArgs e)
    {
        if (RunsList.SelectedItem is not CodexRunRow row) return;
        var audits = await _recorder.FetchAuditsAsync(runUid: row.RunUid, limit: 50);
        AuditsList.ItemsSource = audits.Select(a => new AuditView(a)).ToList();
        if (audits.Count == 0)
            StatusText.Text = $"{row.Ts} 这次运行没有写回题面（只读问答或建议）。";
    }

    /// <summary>把审计行摊平成界面要的两段文本（标题 + 正文一起显示，方便直接对比）。</summary>
    public sealed record AuditView(CodexAuditRow Row)
    {
        public string Header => Row.Header;
        public string Message => string.IsNullOrWhiteSpace(Row.Message) ? "" : "写回结果：" + Row.Message;
        public string BeforeText => Compose(Row.BeforeTitle, Row.BeforeDescription);
        public string AfterText => Compose(Row.AfterTitle, Row.AfterDescription);

        private static string Compose(string title, string desc) =>
            string.IsNullOrWhiteSpace(desc) ? "(空)" : $"{title}\n\n{desc}";
    }
}
