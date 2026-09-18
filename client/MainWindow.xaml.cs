using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using client.Services;

namespace client;

public partial class MainWindow : Window
{
    private readonly ApiClient _api = new();
    private readonly ObservableCollection<CaseResult> _cases = new();
    private Problem? _current;

    public MainWindow()
    {
        InitializeComponent();
        CaseList.ItemsSource = _cases;
        Loaded += async (_, _) => await LoadProblems();
    }

    private async Task LoadProblems()
    {
        ConnBadge.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xE6, 0x7E));
        ConnBadge.Text = "连接中...";
        try
        {
            var problems = await _api.GetProblemsAsync();
            ProblemList.Items.Clear();
            if (problems is { Count: > 0 })
            {
                foreach (var p in problems)
                    ProblemList.Items.Add(new ListBoxItem { Content = $"[{p.Id}] {p.Title}", Tag = p });
                ProblemList.SelectedIndex = 0;
                ConnBadge.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x99, 0x66));
                ConnBadge.Text = $"已连接 · {problems.Count} 题";
            }
            else
            {
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
            DescBox.Text = p.Description;
        }
    }

    private async void SubmitBtn_OnClick(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show("请先在左侧选择题目");
            return;
        }
        var code = CodeBox.Text;
        if (string.IsNullOrWhiteSpace(code))
        {
            MessageBox.Show("请填写代码");
            return;
        }

        SubmitBtn.IsEnabled = false;
        StatusText.Text = "判题中（编译 → 限时运行 → 比对输出）...";
        VerdictText.Text = "结果：判题中...";
        VerdictText.Foreground = Brushes.Gray;
        _cases.Clear();

        try
        {
            var result = await _api.SubmitAsync(_current.Id, code);
            if (result is null)
            {
                VerdictText.Text = "结果：无响应";
                return;
            }

            VerdictText.Text = $"结果：{result.Verdict}   —   {result.Detail}";
            VerdictText.Foreground = result.Verdict == "AC"
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x99, 0x66))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0xD9, 0x53, 0x4F));

            foreach (var c in result.Cases)
                _cases.Add(c);

            StatusText.Text = $"提交 #{result.Id} 完成";
        }
        catch (Exception ex)
        {
            VerdictText.Text = "结果：请求失败";
            StatusText.Text = ex.Message;
        }
        finally
        {
            SubmitBtn.IsEnabled = true;
        }
    }
}
