using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // GeneratorStatus
using System.Windows.Input;
using System.Windows.Threading;             // DispatcherPriority
using HandyControl.Data;

namespace OjUiControls.Controls
{
    /// <summary>
    /// 通用分页列表控件：标题 + ListBox + 页码栏（HandyControl Pagination）。
    /// 服务端题库与客户端题库共用：宿主把全量轻量索引赋给 <see cref="ItemsSource"/>，
    /// 控件内部做内存分页；列表项模板由宿主通过 ItemTemplate 注入。
    /// 双击列表项触发 <see cref="ItemActivated"/>（“双击题目开窗”的统一入口）。
    ///
    /// 每页条数默认「自适应」：按列表可视高度和实际行高算出一屏能放几条，
    /// 窗口缩放时自动重算。固定值（10/20/50/100）可在页码栏下拉框里选。
    /// </summary>
    public partial class PagedListControl : UserControl
    {
        /// <summary>页码栏下拉框里的一个选项；Value = 0 表示自适应。</summary>
        private sealed record PageSizeOption(int Value, string Label)
        {
            public override string ToString() => Label;
        }

        private static readonly PageSizeOption[] PageSizeOptions =
        {
            new(0, "自适应"), new(10, "10"), new(20, "20"), new(50, "50"), new(100, "100")
        };

        private const int FallbackAutoRows = 15;   // 首次布局前量不到列表高度时的兜底行数
        private const double FallbackRowHeight = 34;   // 量不到首行实际高度时的兜底行高（两行式列表项）

        private List<object> _all = new();
        private int _autoRows = FallbackAutoRows;  // 最近一次自适应算出的行数
        private double _rowPitch;                  // 实测行距（0 = 尚未量到）；只由模板决定，量到后一直复用
        private int _autoAdjustBudget;             // 每次数据变更后还允许几次自适应收敛（防布局抖动死循环）
        private bool _ready;   // InitializeComponent 期间 ComboBox 选中项变化不触发分页
        private bool _paging;  // 防重入：Pagination 设置 PageIndex 会回抛 PageUpdated

        /// <summary>选中项变化（单击列表项）。</summary>
        public event EventHandler? ItemSelected;

        /// <summary>列表项被双击（激活）。</summary>
        public event EventHandler? ItemActivated;

        // ---------- 依赖属性 ----------

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register("ItemsSource", typeof(IEnumerable), typeof(PagedListControl),
                new PropertyMetadata(null, OnItemsSourceChanged));

        /// <summary>全量数据源；赋值后自动回到第 1 页。</summary>
        public IEnumerable? ItemsSource
        {
            get => (IEnumerable?)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctl = (PagedListControl)d;
            ctl._all = e.NewValue is IEnumerable src ? src.Cast<object>().ToList() : new List<object>();
            ctl._autoAdjustBudget = 3;   // 数据换了，重新允许几次自适应收敛
            ctl.GoToPage(1);
        }

        public static readonly DependencyProperty PageSizeProperty =
            DependencyProperty.Register("PageSize", typeof(int), typeof(PagedListControl),
                new PropertyMetadata(0, OnPageSizePropertyChanged));

        /// <summary>每页条数；0 = 自适应（按列表可视高度自动决定，默认）。</summary>
        public int PageSize
        {
            get => (int)GetValue(PageSizeProperty);
            set => SetValue(PageSizeProperty, value);
        }

        private static void OnPageSizePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctl = (PagedListControl)d;
            if (!ctl._ready) return;
            int size = (int)e.NewValue;
            var opt = PageSizeOptions.FirstOrDefault(o => o.Value == size);
            if (opt is not null && !ReferenceEquals(ctl.PageSizeBox.SelectedItem, opt))
                ctl.PageSizeBox.SelectedItem = opt;
            ctl.GoToPage(1);
        }

        public static readonly DependencyProperty HeaderTextProperty =
            DependencyProperty.Register("HeaderText", typeof(string), typeof(PagedListControl),
                new PropertyMetadata("", OnHeaderTextChanged));

        /// <summary>列表标题；为空时不占位。</summary>
        public string HeaderText
        {
            get => (string)GetValue(HeaderTextProperty);
            set => SetValue(HeaderTextProperty, value);
        }

        private static void OnHeaderTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctl = (PagedListControl)d;
            string text = e.NewValue as string ?? "";
            ctl.HeaderBlock.Text = text;
            ctl.HeaderBlock.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        public static readonly DependencyProperty ItemTemplateProperty =
            DependencyProperty.Register("ItemTemplate", typeof(DataTemplate), typeof(PagedListControl),
                new PropertyMetadata(null, OnItemTemplateChanged));

        private static void OnItemTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctl = (PagedListControl)d;
            if (ctl.InnerList is null) return;   // 极端时序：模板先于 InitializeComponent 赋值
            ctl.InnerList.ItemTemplate = e.NewValue as DataTemplate;
            ctl._rowPitch = 0;                   // 模板换了，行距要重新量
        }

        /// <summary>列表项模板（宿主注入）。</summary>
        public DataTemplate? ItemTemplate
        {
            get => (DataTemplate?)GetValue(ItemTemplateProperty);
            set => SetValue(ItemTemplateProperty, value);
        }

        // ---------- 只读状态 ----------

        /// <summary>全量条数。</summary>
        public int TotalCount => _all.Count;

        /// <summary>当前页码（从 1 开始）。</summary>
        public int PageIndex => Pager.PageIndex;

        /// <summary>总页数。</summary>
        public int PageCount => Pager.MaxPageCount;

        /// <summary>当前生效的每页条数（自适应时为算出的值）。</summary>
        public int EffectivePageSize => PageSize > 0 ? PageSize : _autoRows;

        /// <summary>当前选中项。</summary>
        public object? SelectedItem => InnerList.SelectedItem;

        /// <summary>最近一次双击的列表项。</summary>
        public object? ActivatedItem { get; private set; }

        public PagedListControl()
        {
            InitializeComponent();
            foreach (var o in PageSizeOptions) PageSizeBox.Items.Add(o);
            PageSizeBox.SelectedItem = PageSizeOptions[0];   // 自适应（此时 _ready 仍为 false）
            _ready = true;
            InnerList.SizeChanged += OnInnerSizeChanged;
            // 首次布局时列表项还没生成，量不到行高；等容器生成完再量一次才准
            InnerList.ItemContainerGenerator.StatusChanged += OnContainersGenerated;
            GoToPage(1);
        }

        /// <summary>重新按当前页码渲染（数据在外部被原地改动后调用）。</summary>
        public void Refresh() => RenderPage();

        // ---------- 内部分页 ----------

        private void GoToPage(int page)
        {
            if (_paging) return;
            _paging = true;
            try
            {
                int size = Math.Max(1, EffectivePageSize);
                int maxPage = Math.Max(1, (int)Math.Ceiling(_all.Count / (double)size));
                if (page < 1) page = 1;
                if (page > maxPage) page = maxPage;
                Pager.MaxPageCount = maxPage;
                Pager.DataCountPerPage = size;
                Pager.PageIndex = page;
                RenderPage();
            }
            finally { _paging = false; }
        }

        private void RenderPage()
        {
            int size = Math.Max(1, EffectivePageSize);
            int page = Math.Max(1, Pager.PageIndex);
            int totalPages = Math.Max(1, (int)Math.Ceiling(_all.Count / (double)size));

            InnerList.ItemsSource = _all.Skip((page - 1) * size).Take(size).ToList();
            InnerList.SelectedItem = null;
            TotalText.Text = $"共 {_all.Count} 条 · 第 {page}/{totalPages} 页";
        }

        /// <summary>按列表可视高度估算一屏能放几行。</summary>
        private int EstimateRowsPerScreen()
        {
            double available = InnerList.ActualHeight;
            if (available <= 0) return _autoRows;

            MeasureRowPitch();   // 量到就缓存进 _rowPitch，量不到则沿用兜底值

            // 向下取整：宁可少一行，也不要因为多一行而整页撑出滚动条
            double rowHeight = _rowPitch > 0 ? _rowPitch : FallbackRowHeight;
            return Math.Max(1, (int)(available / rowHeight));
        }

        /// <summary>
        /// 量一次真实行距并缓存。用相邻两行的位置差（含 ListBoxItem 的 Margin；
        /// ActualHeight 不含 Margin，会低估行距导致算出的行数偏少、列表底部留白）。
        /// 行距只由 ItemTemplate 决定、与每页条数无关，所以量到一次就固定复用 ——
        /// 否则容器还没生成好时会退回兜底值，算出的行数在 11/13 之间来回抖。
        /// </summary>
        private void MeasureRowPitch()
        {
            if (_rowPitch > 0) return;

            var gen = InnerList.ItemContainerGenerator;
            if (gen.ContainerFromIndex(0) is ListBoxItem a && gen.ContainerFromIndex(1) is ListBoxItem b)
            {
                double y0 = a.TranslatePoint(new Point(0, 0), InnerList).Y;
                double y1 = b.TranslatePoint(new Point(0, 0), InnerList).Y;
                double pitch = y1 - y0;
                if (pitch > 1) { _rowPitch = pitch; return; }
            }
            if (gen.ContainerFromIndex(0) is ListBoxItem first && first.ActualHeight > 0)
                _rowPitch = first.ActualHeight;
        }

        // ---------- 事件透传 ----------

        private void OnInnerSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PageSize > 0) return;          // 固定每页条数：不随窗口缩放变化

            int rows = EstimateRowsPerScreen();
            if (rows == _autoRows) return;     // 行数没变就什么都不做，避免布局循环
            _autoRows = rows;
            GoToPage(Pager.PageIndex);
        }

        /// <summary>
        /// 列表项生成完成后重算一次自适应行数。
        /// 首次布局的 SizeChanged 早于容器生成，那时量不到行高、只能退回兜底值，
        /// 而之后 ListBox 高度不再变化、SizeChanged 不会再触发 —— 不在这里补一刀，
        /// 行数就会永久停在一个偏小的值上，列表底部留白（「没铺满」）。
        /// </summary>
        private void OnContainersGenerated(object? sender, EventArgs e)
        {
            if (PageSize > 0) return;
            if (InnerList.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated) return;
            if (_autoAdjustBudget <= 0) return;

            int rows = EstimateRowsPerScreen();
            if (rows == _autoRows) return;     // 已经收敛

            _autoRows = rows;
            _autoAdjustBudget--;
            // 不要在容器生成回调里直接换 ItemsSource：此时正处在布局/生成过程中，
            // 中途替换数据源会把 ItemContainerGenerator 的状态搞乱，列表会变成空白。
            // 推迟到本次布局结束后再重排。
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => GoToPage(Pager.PageIndex)));
        }

        private void OnPageUpdated(object sender, FunctionEventArgs<int> e)
        {
            if (!_ready) return;
            GoToPage(e.Info);
        }

        private void OnPageSizeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            if (PageSizeBox.SelectedItem is PageSizeOption o)
            {
                if (o.Value == 0) _autoRows = EstimateRowsPerScreen();
                PageSize = o.Value;   // 走 DP 统一路径（OnPageSizePropertyChanged 会重新分页）
                GoToPage(1);
            }
        }

        private void OnInnerSelectionChanged(object sender, SelectionChangedEventArgs e)
            => ItemSelected?.Invoke(this, EventArgs.Empty);

        private void OnInnerDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (InnerList.SelectedItem is not { } item) return;
            ActivatedItem = item;
            ItemActivated?.Invoke(this, EventArgs.Empty);
        }
    }
}
