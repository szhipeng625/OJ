using System.Windows;
using System.Windows.Controls;

namespace OjUiControls.Controls
{
    /// <summary>
    /// 通用加载遮罩：覆盖在窗口内容之上，显示「正在加载…」与不确定进度条，
    /// 显示期间挡住下层控件的一切鼠标操作（Background 非空 => 可命中测试）。
    ///
    /// 配合调用方的重入标记使用，可避免「双击 → 异步拉取 → 弹窗」这条链路上
    /// 连点弹出多个窗口：先置 <see cref="IsBusy"/> 再 await，遮罩即刻生效。
    /// </summary>
    public partial class LoadingOverlay : UserControl
    {
        public static readonly DependencyProperty IsBusyProperty =
            DependencyProperty.Register("IsBusy", typeof(bool), typeof(LoadingOverlay),
                new PropertyMetadata(false, OnIsBusyChanged));

        /// <summary>是否正在加载（true 时遮罩可见并拦截输入）。</summary>
        public bool IsBusy
        {
            get => (bool)GetValue(IsBusyProperty);
            set => SetValue(IsBusyProperty, value);
        }

        private static void OnIsBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var ctl = (LoadingOverlay)d;
            ctl.Root.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
            if ((bool)e.NewValue)
                ctl.Focus();   // 把键盘焦点从下层控件移开
        }

        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register("Message", typeof(string), typeof(LoadingOverlay),
                new PropertyMetadata("正在加载…", OnMessageChanged));

        /// <summary>遮罩上的提示文字。</summary>
        public string Message
        {
            get => (string)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((LoadingOverlay)d).MessageText.Text = e.NewValue as string ?? "";

        public LoadingOverlay()
        {
            InitializeComponent();
        }
    }
}
