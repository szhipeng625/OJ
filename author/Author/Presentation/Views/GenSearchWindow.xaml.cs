using System.Windows;
using author.DataAccess.Models;

namespace author.Presentation.Views;

/// <summary>跨生成器查找 gen.cpp 代码的结果窗口。</summary>
public partial class GenSearchWindow : Window
{
    public GenSearchHit? Selected { get; private set; }

    public GenSearchWindow(List<GenSearchHit> hits, string keyword)
    {
        InitializeComponent();
        HitList.ItemsSource = hits;
        SummaryText.Text = $"关键字“{keyword}”共匹配 {hits.Count} 处（双击条目或点「打开该生成器」打开编辑器）：";
        if (hits.Count > 0) HitList.SelectedIndex = 0;
    }

    private void OnGo(object sender, RoutedEventArgs e)
    {
        if (HitList.SelectedItem is GenSearchHit h)
        {
            Selected = h;
            DialogResult = true;
        }
    }
}
