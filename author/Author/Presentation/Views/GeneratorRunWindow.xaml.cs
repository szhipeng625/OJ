using System.Windows;
using System.Windows.Controls;
using author.Business.Services;

namespace author.Presentation.Views;

/// <summary>
/// 生成数据窗口：展示每个勾选的数据生成器的生成情况。
/// 左侧 TreeView 只先加载生成器目录，点击展开时才读取文件列表（大数据不全量读入）；
/// 点击文件后在右侧只读预览，超过 200KB 由后端截断。
/// </summary>
public partial class GeneratorRunWindow : Window
{
    private readonly Workbench _wb;
    private readonly int _pid;
    private readonly IReadOnlyList<(int Id, string Name)> _gens;
    private readonly int _count;

    private readonly Dictionary<int, TreeViewItem> _genNodes = new();
    private readonly HashSet<int> _loadedGens = new();
    private int _doneCount;
    private int _okCount;
    private bool _closed;

    private sealed record NodeTag(string Kind, int Id, string? Name, string? File);

    public GeneratorRunWindow(Workbench wb, int problemId, IReadOnlyList<(int Id, string Name)> gens, int count)
    {
        InitializeComponent();
        _wb = wb;
        _pid = problemId;
        _gens = gens;
        _count = count;
        Title = $"P{problemId} 生成数据";
        SummaryText.Text = $"正在为 {gens.Count} 个数据生成器生成数据，每组 {count} 组……";
        Loaded += async (_, _) => await RunAllAsync();
    }

    private async Task RunAllAsync()
    {
        foreach (var (id, name) in _gens)
        {
            if (_closed) return;
            var node = AddGenNode(id, name);
            bool ok = false;
            try
            {
                var r = await _wb.Generators.RunAsync(_pid, id, name, _count);
                ok = r.Ok;
                if (_closed) return;
                node.Header = $"{name} — {(r.Ok ? "成功" : "失败")}";
            }
            catch (Exception ex)
            {
                if (_closed) return;
                node.Header = $"{name} — 失败";
                node.Items.Clear();
                node.Items.Add(new TreeViewItem { Header = ex.Message });
            }
            if (ok) _okCount++;
            _doneCount++;
            UpdateSummary();
        }
    }

    private TreeViewItem AddGenNode(int id, string name)
    {
        var item = new TreeViewItem
        {
            Header = $"{name} — 运行中…",
            Tag = new NodeTag("gen", id, name, null),
            IsExpanded = false
        };
        // 懒加载占位：首次展开时才读取文件目录
        item.Items.Add(new TreeViewItem { Header = "加载中…", Tag = new NodeTag("dummy", id, name, null) });
        item.Expanded += OnGenExpanded;
        GenTree.Items.Add(item);
        _genNodes[id] = item;
        return item;
    }

    private async void OnGenExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node || node.Tag is not NodeTag tag || tag.Kind != "gen") return;
        string? name = tag.Name;
        if (string.IsNullOrEmpty(name) || !_loadedGens.Add(tag.Id)) return;

        var files = await _wb.Generators.FilesAsync(_pid, name);
        if (_closed) return;
        node.Items.Clear();
        if (files.Count == 0)
        {
            node.Items.Add(new TreeViewItem { Header = "（暂无数据文件）" });
            return;
        }
        foreach (var f in files)
        {
            node.Items.Add(new TreeViewItem
            {
                Header = f.Display,
                Tag = new NodeTag("file", tag.Id, name, f.Name)
            });
        }
    }

    private async void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem item || item.Tag is not NodeTag tag || tag.Kind != "file") return;
        if (string.IsNullOrEmpty(tag.Name) || string.IsNullOrEmpty(tag.File)) return;

        PreviewTitle.Text = $"文件：{tag.File}";
        PreviewBox.Text = "正在读取…";
        var (text, truncated) = await _wb.Generators.GetFileAsync(_pid, tag.Name, tag.File);
        if (_closed) return;
        PreviewBox.Text = truncated
            ? text + "\n……（文件过大，仅显示前 200KB）"
            : text;
    }

    private void UpdateSummary()
    {
        if (_doneCount < _gens.Count)
            SummaryText.Text = $"生成进度：{_doneCount}/{_gens.Count}（成功 {_okCount}）……";
        else
            SummaryText.Text = $"全部完成：成功 {_okCount}/{_gens.Count} 个生成器。如需生成 .out 答案，请到「生成标准答案」页。";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }
}
