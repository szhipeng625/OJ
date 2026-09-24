using System;
using System.Windows;
using Microsoft.Win32;

namespace OJInstaller
{
    public partial class MainWindow : Window
    {
        private readonly InstallerCore _core;
        private bool _running;

        public MainWindow(InstallerCore core)
        {
            InitializeComponent();
            _core = core;

            // 后台线程的回调统一封送到界面线程
            _core.Log = line => Dispatcher.Invoke(() => AppendLog(line));
            _core.Progress = (got, total) => Dispatcher.Invoke(() => UpdateProgress(got, total));

            ClientBox.IsChecked = core.InstallClient;
            ServerBox.IsChecked = core.InstallServer;
            DesktopShortcutBox.IsChecked = core.CreateDesktopShortcuts;
            DirBox.Text = core.InstallRoot;
            UrlBox.Text = core.BaseUrl;
        }

        private void AppendLog(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private void UpdateProgress(long got, long total)
        {
            if (total <= 0)
            {
                Progress.IsIndeterminate = true;
                StatusText.Text = "下载中...";
                return;
            }
            Progress.IsIndeterminate = false;
            double pct = Math.Min(100.0, got * 100.0 / total);
            Progress.Value = pct;
            StatusText.Text = $"下载中 {InstallerCore.Readable(got)} / {InstallerCore.Readable(total)}（{pct:0}%）";
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择安装目录",
                InitialDirectory = string.IsNullOrWhiteSpace(DirBox.Text) ? null : DirBox.Text
            };
            if (dlg.ShowDialog() == true)
                DirBox.Text = dlg.FolderName;
        }

        private async void OnInstall(object sender, RoutedEventArgs e)
        {
            if (_running) return;

            if (ClientBox.IsChecked != true && ServerBox.IsChecked != true)
            {
                MessageBox.Show("请至少选择一个组件。", "ACMDOG", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string dir = DirBox.Text.Trim();
            string url = UrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir))
            {
                MessageBox.Show("请填写安装目录。", "ACMDOG", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("请填写下载地址。", "ACMDOG", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _core.InstallRoot = dir;
            _core.BaseUrl = InstallerCore.EnsureTrailingSlash(url);
            _core.InstallClient = ClientBox.IsChecked == true;
            _core.InstallServer = ServerBox.IsChecked == true;
            _core.CreateDesktopShortcuts = DesktopShortcutBox.IsChecked == true;

            _running = true;
            SetBusy(true);
            Progress.Value = 0;
            Progress.IsIndeterminate = false;
            StatusText.Text = "开始安装...";
            try
            {
                bool ok = await _core.InstallAsync();
                if (ok)
                {
                    StatusText.Text = "✔ 安装完成";
                    MessageBox.Show("安装完成！快捷方式已创建到桌面与开始菜单。",
                        "ACMDOG", MessageBoxButton.OK, MessageBoxImage.Information);
                    Close();
                }
                else
                {
                    StatusText.Text = "安装失败，请查看上方日志。";
                    MessageBox.Show("安装失败，请检查下载地址与网络后重试。",
                        "ACMDOG", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog("异常: " + ex.Message);
                StatusText.Text = "安装出错";
                MessageBox.Show("安装出错: " + ex.Message,
                    "ACMDOG", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _running = false;
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            InstallBtn.IsEnabled = !busy;
            CancelBtn.IsEnabled = !busy;
            ClientBox.IsEnabled = !busy;
            ServerBox.IsEnabled = !busy;
            DirBox.IsEnabled = !busy;
            BrowseBtn.IsEnabled = !busy;
            UrlBox.IsEnabled = !busy;
            InstallBtn.Content = busy ? "安装中..." : "开始安装";
        }

        private void OnCancel(object sender, RoutedEventArgs e) => Close();
    }
}
