using System;
using System.Windows;

namespace OJInstaller
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var core = new InstallerCore();
            bool uninstall = false, silent = false, runAfter = false, help = false;

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
                    case "uninstall": case "u": uninstall = true; break;
                    case "install": case "i": uninstall = false; break;
                    case "silent": case "s": silent = true; break;
                    case "url": core.BaseUrl = val; break;
                    case "dir": core.InstallRoot = val; break;
                    case "pkg": core.PackageName = val; break;
                    case "run": runAfter = true; break;
                    case "help": case "h": case "?": help = true; break;
                }
            }

            if (help)
            {
                MessageBox.Show(
                    "用法: OJInstaller [选项]\n\n" +
                    "  无参数            打开图形安装界面\n" +
                    "  /uninstall        一键卸载\n" +
                    "  /silent           静默安装（不显示界面）\n" +
                    "  /url=<地址>       指定安装包下载地址前缀（以 / 结尾）\n" +
                    "  /pkg=<文件名>     指定安装包文件名（默认 oj-package.zip）\n" +
                    "  /dir=<目录>       指定安装目录（默认 %LocalAppData%\\OJ）\n" +
                    "  /run              安装完成后自动启动客户端",
                    "ACMOJ下载器", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }

            if (uninstall)
            {
                if (!silent)
                {
                    var r = MessageBox.Show("确定要卸载 ACMDOG 吗？", "ACMOJ下载器",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) return 0;
                }
                core.Uninstall(null);
                if (!silent)
                    MessageBox.Show("卸载完成。", "ACMOJ下载器", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }

            if (silent)
            {
                var ok = core.InstallAsync(null, null, runAfter).GetAwaiter().GetResult();
                return ok ? 0 : 1;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new MainWindow(core));
            return 0;
        }
    }
}
