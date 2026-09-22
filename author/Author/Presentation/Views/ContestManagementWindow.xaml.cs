using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using author.Business.Services;
using author.DataAccess.Models;

namespace author.Presentation.Views;

/// <summary>
/// 比赛管理独立窗口：列出已有比赛；新建 / 编辑比赛在独立窗口中进行。
/// </summary>
public partial class ContestManagementWindow : Window
{
    private readonly Workbench _wb;

    public ContestManagementWindow(Workbench workbench)
    {
        InitializeComponent();
        _wb = workbench;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var contests = await _wb.Contests.ListAsync();
            ContestList.ItemsSource = null;
            ContestList.ItemsSource = contests;
            StatusText.Text = $"共 {contests.Count} 场比赛（双击可编辑）";
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败：" + ex.Message;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnNewContest(object sender, RoutedEventArgs e)
    {
        var win = new ContestEditWindow(_wb, null) { Owner = this };
        win.ShowDialog();
        _ = RefreshAsync();
    }

    private void OnContestDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ContestList.SelectedItem is ContestInfo c)
        {
            var win = new ContestEditWindow(_wb, c.Id) { Owner = this };
            win.ShowDialog();
            _ = RefreshAsync();
        }
    }
}

/// <summary>题目勾选条目（用于比赛管理窗口的题目列表）。</summary>
public sealed class ProblemCheckItem : INotifyPropertyChanged
{
    private bool _isChecked;
    public int Id { get; }
    public string Title { get; }

    public ProblemCheckItem(int id, string title)
    {
        Id = id;
        Title = title;
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
