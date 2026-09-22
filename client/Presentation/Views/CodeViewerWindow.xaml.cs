using System.Linq;
using System.Windows;
using client.DataAccess.Models;

namespace client.Presentation.Views;

/// <summary>
/// 只读代码查看窗口：比赛提交记录双击后展示该次提交的代码与测试点详情。
/// </summary>
public partial class CodeViewerWindow : Window
{
    public CodeViewerWindow(string title, string header, SubmissionDetail detail)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        CodeBox.Text = detail.Code;
        CaseList.ItemsSource = detail.Cases.Select(c => new
        {
            Name = c.Name,
            Result = c.Passed ? "通过" : "失败",
            Time = c.TimeMs + "ms",
            Info = c.Info
        }).ToList();
    }
}
