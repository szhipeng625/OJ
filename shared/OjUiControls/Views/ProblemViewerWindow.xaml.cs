using System.Windows;
using OjUiControls.Helpers;
using OjUiControls.Models;

namespace OjUiControls.Views
{
    /// <summary>
    /// 只读题面窗口：展示题目描述、样例与元信息，底部主按钮触发后续动作
    /// （客户端「开始做题」→ SolveWindow；出题端可复用为打开工作台）。
    ///
    /// 用法：<c>if (new ProblemViewerWindow(data){Owner=this}.ShowDialog() == true) { /* 主按钮动作 */ }</c>
    /// </summary>
    public partial class ProblemViewerWindow : Window
    {
        public ProblemViewerWindow(ProblemViewData data)
        {
            InitializeComponent();
            Title = $"P{data.Id} {data.Title}";
            TitleText.Text = $"P{data.Id}  {data.Title}";
            MetaText.Text = data.MetaText;
            PrimaryBtn.Content = string.IsNullOrWhiteSpace(data.PrimaryActionText) ? "开始做题" : data.PrimaryActionText;
            Loaded += (_, _) => MarkdownRenderer.Render(StatementBox, data.StatementMarkdown);
        }

        private void OnPrimaryAction(object sender, RoutedEventArgs e) => Finish(true);

        private void OnClose(object sender, RoutedEventArgs e) => Finish(false);

        private void Finish(bool primary)
        {
            // 非 ShowDialog（如 Show()）调用时设置 DialogResult 会抛异常，此时仅关闭窗口
            try { DialogResult = primary; }
            catch (System.InvalidOperationException) { }
            Close();
        }
    }
}
