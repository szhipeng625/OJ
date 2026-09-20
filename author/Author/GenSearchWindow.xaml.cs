using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace author;

/// <summary>跨题查找 gen.cpp 的一条命中。</summary>
public record SearchHit(int Pid, string Title, int LineNo, string Line, string Keyword);

public partial class GenSearchWindow : Window
{
    public SearchHit? Selected { get; private set; }

    public GenSearchWindow(System.Collections.Generic.List<SearchHit> hits, string keyword)
    {
        InitializeComponent();
        HitList.ItemsSource = hits;
        SummaryText.Text = $"关键字“{keyword}”共匹配 {hits.Count} 处（双击条目或点「转到该题」跳转）：";
        if (hits.Count > 0) HitList.SelectedIndex = 0;
    }

    private void OnGo(object sender, RoutedEventArgs e)
    {
        if (HitList.SelectedItem is SearchHit h)
        {
            Selected = h;
            DialogResult = true;
        }
    }
}
