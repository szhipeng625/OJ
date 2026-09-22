using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using author.Business.Services;
using author.DataAccess.Models;

namespace author.Presentation.Views;

/// <summary>
/// 比赛创建 / 编辑窗口：新建时编号自动生成，保存后上传到 MySQL 数据库并本地复制。
/// </summary>
public partial class ContestEditWindow : Window
{
    private readonly Workbench _wb;
    private readonly int? _editId;
    private int _newId;
    private List<ProblemCheckItem> _problems = new();

    private static readonly string[] TimeOptions = BuildTimeOptions();

    public bool Saved { get; private set; }

    public ContestEditWindow(Workbench workbench, int? editId)
    {
        InitializeComponent();
        _wb = workbench;
        _editId = editId;
        ContestStartTime.ItemsSource = TimeOptions;
        ContestEndTime.ItemsSource = TimeOptions;
        var now = DateTime.Now;
        SetStartEnd(now.Date.AddHours(9), now.Date.AddHours(11));
        Loaded += OnLoaded;
    }

    private static string[] BuildTimeOptions()
    {
        var list = new List<string>();
        for (int h = 0; h < 24; h++)
            for (int m = 0; m < 60; m += 30)
                list.Add($"{h:00}:{m:00}");
        return list.ToArray();
    }

    private static DateTime ParseDateTime(string? s)
    {
        if (!DateTime.TryParse(s, out var dt))
            dt = DateTime.Now.Date.AddHours(9);
        int totalMin = (dt.Hour * 60 + dt.Minute + 15) / 30 * 30;
        return dt.Date.AddMinutes(totalMin);
    }

    private void SetStartEnd(DateTime start, DateTime end)
    {
        ContestStartDate.SelectedDate = start.Date;
        ContestStartTime.SelectedItem = start.ToString("HH:mm");
        ContestEndDate.SelectedDate = end.Date;
        ContestEndTime.SelectedItem = end.ToString("HH:mm");
    }

    private (string start, string end) ReadStartEnd()
    {
        var sd = ContestStartDate.SelectedDate ?? DateTime.Now.Date;
        var ed = ContestEndDate.SelectedDate ?? DateTime.Now.Date;
        string st = ContestStartTime.SelectedItem as string ?? "09:00";
        string et = ContestEndTime.SelectedItem as string ?? "11:00";
        return ($"{sd:yyyy-MM-dd} {st}", $"{ed:yyyy-MM-dd} {et}");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var problems = await _wb.Problems.ListAsync();
        _problems = problems.Select(p => new ProblemCheckItem(p.Id, p.Title)).ToList();
        ProblemCheckList.ItemsSource = _problems;

        if (_editId is int id)
        {
            Title = $"编辑比赛 C{id}";
            IdText.Text = $"比赛编号：C{id}";
            var draft = await _wb.Contests.GetDraftAsync(id);
            if (draft is not null)
            {
                ContestNameBox.Text = draft.Name;
                SetStartEnd(ParseDateTime(draft.StartTime), ParseDateTime(draft.EndTime));
                var ids = new HashSet<int>(draft.ProblemIds);
                foreach (var p in _problems) p.IsChecked = ids.Contains(p.Id);
            }
        }
        else
        {
            _newId = await _wb.Contests.SuggestIdAsync();
            Title = "新建比赛";
            IdText.Text = $"比赛编号：C{_newId}（自动生成）";
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

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        string name = ContestNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            StatusText.Text = "请输入比赛名称";
            return;
        }
        int cid = _editId ?? _newId;
        var (start, end) = ReadStartEnd();
        var ids = _problems.Where(p => p.IsChecked).Select(p => p.Id).ToArray();
        var draft = new ContestDraft(cid, name, start, end, ids);

        IsEnabled = false;
        StatusText.Text = "正在保存并发布比赛…";
        try
        {
            var r = await _wb.Contests.SaveAndPublishAsync(draft, _wb.ServerRoot);
            StatusText.Text = r.Message;
            if (r.Ok)
            {
                Saved = true;
                MessageBox.Show(this, r.Message, "比赛管理", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
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
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
