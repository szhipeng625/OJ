using System.Windows;

namespace author.Presentation.Views;

/// <summary>新建题目弹窗：输入标题与标签，保存后关闭。</summary>
public partial class NewProblemWindow : Window
{
    public string? ProblemTitle { get; private set; }
    public string? ProblemTags { get; private set; }

    public NewProblemWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TitleBox.Focus();
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        string title = TitleBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            MessageBox.Show(this, "题目标题不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ProblemTitle = title;
        ProblemTags = TagsBox.Text.Trim();
        DialogResult = true;
    }
}
