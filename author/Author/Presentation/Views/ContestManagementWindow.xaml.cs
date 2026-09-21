using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using author.Business.Services;
using author.DataAccess.Models;

namespace author.Presentation.Views;

/// <summary>
/// 比赛管理独立窗口：录入题目时给出题目列表勾选，保存后自动发布到客户端 contest 目录。
/// </summary>
public partial class ContestManagementWindow : Window
{
    private readonly Workbench _wb;
    private List<ContestInfo> _contests = new();
    private List<ProblemCheckItem> _problems = new();

    public ContestManagementWindow(Workbench workbench)
    {
        InitializeComponent();
        _wb = workbench;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        TargetText.Text = string.IsNullOrWhiteSpace(_wb.ServerRoot)
            ? "客户端 contest 目录：未配置"
            : $"保存后自动发布到：{_wb.ServerRoot}\\contests\\{{编号}}\\contest.json";
        await RefreshAllAsync(suggestNew: true);
    }

    private async Task RefreshAllAsync(bool suggestNew)
    {
        try
        {
            var problems = await _wb.Problems.ListAsync();
            var checkedIds = new HashSet<int>(_problems.Where(p => p.IsChecked).Select(p => p.Id));
            _problems = problems.Select(p => new ProblemCheckItem(p.Id, p.Title)
            {
                IsChecked = checkedIds.Contains(p.Id)
            }).ToList();
            ProblemCheckList.ItemsSource = _problems;

            _contests = await _wb.Contests.ListAsync();
            ContestList.ItemsSource = null;
            ContestList.ItemsSource = _contests;
            ContestList.DisplayMemberPath = nameof(ContestInfo.Display);

            if (suggestNew && (string.IsNullOrWhiteSpace(ContestIdBox.Text) || _contests.Count == 0))
            {
                int next = await _wb.Contests.SuggestIdAsync();
                ContestIdBox.Text = next.ToString();
            }
            StatusText.Text = $"已加载 {_problems.Count} 道题、{_contests.Count} 场比赛";
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败：" + ex.Message;
        }
    }

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var p in _problems) p.IsChecked = true;
    }

    private void OnCheckNone(object sender, RoutedEventArgs e)
    {
        foreach (var p in _problems) p.IsChecked = false;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAllAsync(suggestNew: false);

    private void OnNew(object sender, RoutedEventArgs e)
    {
        ContestIdBox.Text = "";
        ContestNameBox.Text = "";
        ContestStartBox.Text = "";
        ContestEndBox.Text = "";
        foreach (var p in _problems) p.IsChecked = false;
        _ = SuggestIdAsync();
    }

    private async Task SuggestIdAsync()
    {
        int next = await _wb.Contests.SuggestIdAsync();
        ContestIdBox.Text = next.ToString();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ContestIdBox.Text.Trim(), out int cid) || cid <= 0)
        {
            MessageBox.Show(this, "请输入合法的比赛编号（正整数）", "比赛管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string name = ContestNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "请输入比赛名称", "比赛管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ids = _problems.Where(p => p.IsChecked).Select(p => p.Id).ToArray();
        var draft = new ContestDraft(cid, name, ContestStartBox.Text.Trim(), ContestEndBox.Text.Trim(), ids);

        IsEnabled = false;
        StatusText.Text = "正在保存并发布比赛…";
        try
        {
            var r = await _wb.Contests.SaveAndPublishAsync(draft, _wb.ServerRoot);
            StatusText.Text = r.Message;
            if (r.Ok)
            {
                MessageBox.Show(this, r.Message, "比赛管理", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAllAsync(suggestNew: false);
            }
            else
            {
                MessageBox.Show(this, r.Message, "比赛管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败：" + ex.Message;
        }
        finally { IsEnabled = true; }
    }

    private async void OnSelectContest(object sender, SelectionChangedEventArgs e)
    {
        if (ContestList.SelectedItem is not ContestInfo c) return;
        try
        {
            var draft = await _wb.Contests.GetDraftAsync(c.Id);
            if (draft is null) return;

            ContestIdBox.Text = draft.Id.ToString();
            ContestNameBox.Text = draft.Name;
            ContestStartBox.Text = draft.StartTime;
            ContestEndBox.Text = draft.EndTime;

            var ids = new HashSet<int>(draft.ProblemIds);
            foreach (var p in _problems) p.IsChecked = ids.Contains(p.Id);

            StatusText.Text = $"已载入比赛 C{c.Id}，可修改后重新保存";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取比赛失败：" + ex.Message;
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
