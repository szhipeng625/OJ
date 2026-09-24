using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace OJInstaller
{
    public partial class MainWindow : Window
    {
        private readonly InstallerCore _core;
        private bool _busy;

        public MainWindow(InstallerCore core)
        {
            InitializeComponent();
            _core = core;
            DirBox.Text = _core.ResolveInstallRoot();
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { InitialDirectory = DirBox.Text.Trim() };
            if (dlg.ShowDialog(this) == true)
                DirBox.Text = dlg.FolderName;
        }

        private async void OnInstall(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;

            BtnInstall.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            ChkDesktop.IsEnabled = false;
            DirBox.IsEnabled = false;
            LogBox.Text = "";
            Progress.IsIndeterminate = false;
            Progress.Value = 0;

            _core.InstallRoot = DirBox.Text.Trim();
            _core.CreateDesktopShortcuts = ChkDesktop.IsChecked == true;

            var ok = await Task.Run(() => _core.InstallAsync(
                msg => Dispatcher.Invoke(() => AppendLog(msg)),
                (got, total) => Dispatcher.Invoke(() => SetProgress(got, total)),
                runAfter: false));

            if (ok)
            {
                if (!InstallerCore.HasDesktopRuntime8())
                {
                    var r = MessageBox.Show(this,
                        "未检测到 .NET 8 桌面运行时（运行客户端/服务端需要）。是否打开下载页面？",
                        "ACMOJ下载器", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (r == MessageBoxResult.Yes)
                        InstallerCore.OpenUrl("https://dotnet.microsoft.com/download/dotnet/8.0");
                }

                MessageBox.Show(this, "安装成功！", "ACMOJ下载器",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            else
            {
                MessageBox.Show(this, "安装失败，请查看日志。", "ACMOJ下载器",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                BtnInstall.IsEnabled = true;
                BtnBrowse.IsEnabled = true;
                ChkDesktop.IsEnabled = true;
                DirBox.IsEnabled = true;
            }

            _busy = false;
        }

        private void OnExit(object sender, RoutedEventArgs e) => Close();

        private void AppendLog(string msg)
        {
            LogBox.AppendText(msg + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private void SetProgress(long got, long? total)
        {
            if (total.HasValue && total.Value > 0)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = Math.Min(100, got * 100.0 / total.Value);
            }
            else
            {
                Progress.IsIndeterminate = true;
            }
        }
    }
}
