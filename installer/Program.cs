using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace OJInstaller
{
    /// <summary>
    /// 安装器入口：
    ///   无参数             → 图形化安装界面（双击 exe 即出现下载界面）
    ///   /uninstall         → 图形化卸载（确认对话框）
    ///   /silent            → 静默安装（无界面，用于自动化）
    ///   /silent /uninstall → 静默卸载
    /// 下载地址优先级：/url= &gt; installer_url.txt &gt; 打包烧录的 InstallerBaseUrl。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool uninstall = false;
            bool silent = false;
            bool clientOnly = false;
            bool serverOnly = false;
            string url = null;
            string dir = null;

            foreach (var raw in args)
            {
                var s = raw;
                if (s.StartsWith("--", StringComparison.Ordinal)) s = s.Substring(2);
                else if (s.StartsWith("/", StringComparison.Ordinal) || s.StartsWith("-", StringComparison.Ordinal)) s = s.Substring(1);

                var i = s.IndexOf('=');
                var key = (i >= 0 ? s.Substring(0, i) : s).Trim().ToLowerInvariant();
                var val = i >= 0 ? s.Substring(i + 1) : null;

                switch (key)
                {
                    case "uninstall":
                    case "u":
                        uninstall = true;
                        break;
                    case "install":
                    case "i":
                        uninstall = false;
                        break;
                    case "silent":
                    case "s":
                        silent = true;
                        break;
                    case "url":
                        url = val;
                        break;
                    case "dir":
                        dir = val;
                        break;
                    case "client":
                        clientOnly = true;
                        break;
                    case "server":
                    case "author":
                        serverOnly = true;
                        break;
                }
            }

            var core = new InstallerCore
            {
                BaseUrl = ResolveBaseUrl(url),
                InstallRoot = ResolveInstallRoot(dir),
                InstallClient = serverOnly ? false : true,
                InstallServer = clientOnly ? false : true,
            };

            if (uninstall)
            {
                if (silent) return core.Uninstall() ? 0 : 1;
                return RunUninstallDialog(core);
            }

            if (silent)
                return core.InstallAsync().GetAwaiter().GetResult() ? 0 : 1;

            // 图形化安装界面（双击 exe 直接出现下载界面）
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new MainWindow(core));
            return 0;
        }

        private static int RunUninstallDialog(InstallerCore core)
        {
            var res = MessageBox.Show(
                "确定要卸载 ACMDOG 吗？\n\n将删除：桌面/开始菜单快捷方式、卸载注册表项，以及安装目录：\n" + core.InstallRoot,
                "ACMDOG 卸载",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return 0;

            core.Uninstall();

            MessageBox.Show("卸载完成。", "ACMDOG 卸载", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        private static string ResolveBaseUrl(string urlOverride)
        {
            if (!string.IsNullOrWhiteSpace(urlOverride)) return InstallerCore.EnsureTrailingSlash(urlOverride);

            var side = Path.Combine(AppContext.BaseDirectory, "installer_url.txt");
            if (File.Exists(side))
            {
                var t = File.ReadAllText(side).Trim();
                if (!string.IsNullOrWhiteSpace(t)) return InstallerCore.EnsureTrailingSlash(t);
            }

            var def = typeof(Program).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "InstallerBaseUrl")?.Value
                ?? "http://YOUR-SERVER/oj/";
            return InstallerCore.EnsureTrailingSlash(def);
        }

        private static string ResolveInstallRoot(string dirOverride)
        {
            if (!string.IsNullOrWhiteSpace(dirOverride)) return dirOverride;

            // 若以 uninstall.exe 运行（自身被复制到安装根目录），安装根目录即自身所在目录
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self) &&
                string.Equals(Path.GetFileName(self), "uninstall.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(self);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OJ");
        }
    }
}
