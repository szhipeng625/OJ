using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace OJInstaller
{
    /// <summary>
    /// OJ 一键安装 / 卸载引导程序。
    /// 安装：从服务器下载 oj-package.zip（client/ + server/）解压到安装目录，
    ///       创建桌面/开始菜单快捷方式，写入卸载注册表项，复制自身为 uninstall.exe。
    /// 卸载：结束进程、删除快捷方式与注册表项、删除安装目录（含自身，延后删除）。
    /// </summary>
    internal static class Program
    {
        private const string DefaultPackageName = "oj-package.zip";
        private const string ClientExe = "client.exe";
        private const string ServerExe = "Author.exe";

        private static string _baseUrl;
        private static string _installRoot;
        private static string _packageName = DefaultPackageName;
        private static bool _silent;
        private static bool _runAfter;

        private static string DefaultBaseUrl =>
            typeof(Program).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "InstallerBaseUrl")?.Value
            ?? "http://YOUR-SERVER/oj/";

        private static async Task<int> Main(string[] args)
        {
            var uninstall = false;

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
                        _silent = true;
                        break;
                    case "url":
                        _baseUrl = val;
                        break;
                    case "dir":
                        _installRoot = val;
                        break;
                    case "pkg":
                        _packageName = val;
                        break;
                    case "run":
                        _runAfter = true;
                        break;
                    case "help":
                    case "h":
                    case "?":
                        PrintHelp();
                        return 0;
                }
            }

            _baseUrl = ResolveBaseUrl();
            _installRoot = ResolveInstallRoot();

            return uninstall ? DoUninstall() : await DoInstall();
        }

        // ---------------------------------------------------------------- 安装

        private static async Task<int> DoInstall()
        {
            Header("OJ 一键安装（客户端 + 服务端）");

            Console.WriteLine("  安装目录: " + _installRoot);
            Console.WriteLine("  下载地址: " + _baseUrl + _packageName);
            Console.WriteLine();

            if (Directory.Exists(_installRoot))
            {
                Console.WriteLine("  [!] 检测到已安装，将覆盖安装。");
                KillApps();
            }

            // 1. 下载安装包
            var zip = Path.Combine(Path.GetTempPath(), _packageName);
            var url = _baseUrl + _packageName;
            if (!await DownloadAsync(url, zip))
            {
                Pause();
                return 1;
            }

            // 2. 可选 SHA256 校验
            await TryVerifyAsync(url + ".sha256", zip);

            // 3. 解压
            if (!Extract(zip, _installRoot))
            {
                Pause();
                return 1;
            }

            // 4. 复制自身为卸载器
            var uninstaller = CopySelfToUninstaller();

            // 5. 快捷方式 + 注册表
            CreateShortcuts(uninstaller);
            WriteRegistry(uninstaller);

            // 6. 完整性检查
            var cExe = Path.Combine(_installRoot, "client", ClientExe);
            var sExe = Path.Combine(_installRoot, "server", ServerExe);
            if (!File.Exists(cExe) || !File.Exists(sExe))
            {
                Console.WriteLine("  [!] 警告：未找到 client/client.exe 或 server/Author.exe。");
                Console.WriteLine("      请确认安装包内为 client/ 与 server/ 两个子目录。");
            }

            // 7. .NET 8 桌面运行时检查（客户端/服务端为框架依赖，运行需要）
            if (!HasDesktopRuntime8())
            {
                Console.WriteLine();
                Console.WriteLine("  [!] 未检测到 .NET 8 桌面运行时（运行客户端/服务端需要）。");
                Console.WriteLine("      下载地址: https://dotnet.microsoft.com/download/dotnet/8.0");
                if (!_silent)
                {
                    Console.Write("  是否现在打开下载页面？(y/N): ");
                    var k = ReadKey();
                    if (k == 'y' || k == 'Y')
                    {
                        OpenUrl("https://dotnet.microsoft.com/download/dotnet/8.0");
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("  [完成] 安装成功。");
            Console.WriteLine("    桌面 / 开始菜单已创建：OJ客户端、OJ服务端、卸载 OJ。");

            if (_runAfter)
            {
                Launch(cExe);
            }
            else if (!_silent)
            {
                Console.Write("  是否立即启动？[1]客户端 [2]服务端 [回车]跳过: ");
                var k = ReadKey();
                if (k == '1') Launch(cExe);
                else if (k == '2') Launch(sExe);
            }

            Pause();
            return 0;
        }

        private static async Task<bool> DownloadAsync(string url, string dest)
        {
            Console.WriteLine("  下载: " + url);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                var total = resp.Content.Headers.ContentLength ?? 0;
                using var src = await resp.Content.ReadAsStreamAsync();
                using var fs = File.Create(dest);

                var buf = new byte[256 * 1024];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await fs.WriteAsync(buf, 0, n);
                    got += n;
                    if (!_silent && total > 0)
                    {
                        Console.Write($"\r  {Readable(got)} / {Readable(total)}  ({got * 100 / total}%)   ");
                    }
                }

                Console.WriteLine(_silent ? "" : "\r  下载完成。                    ");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [失败] 下载失败: " + ex.Message);
                Console.WriteLine("  请确认浏览器能打开: " + url);
                return false;
            }
        }

        private static async Task TryVerifyAsync(string url, string file)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var text = await client.GetStringAsync(url);
                var hash = text.Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(hash)) return;

                using var sha = SHA256.Create();
                using var fs = File.OpenRead(file);
                var actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();

                Console.WriteLine(string.Equals(actual, hash.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
                    ? "  [OK] SHA256 校验通过。"
                    : "  [!] SHA256 校验不一致，但继续安装。");
            }
            catch
            {
                // 校验文件可选，缺失/失败不阻断
            }
        }

        private static bool Extract(string zip, string root)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    try
                    {
                        Directory.Delete(root, true);
                    }
                    catch
                    {
                        // 自身可能位于 root 内，退化为清空子目录/文件
                        foreach (var d in Directory.GetDirectories(root))
                        {
                            try { Directory.Delete(d, true); } catch { /* ignore */ }
                        }
                        foreach (var f in Directory.GetFiles(root))
                        {
                            try { File.Delete(f); } catch { /* ignore */ }
                        }
                    }
                }

                Directory.CreateDirectory(root);
                ZipFile.ExtractToDirectory(zip, root, true);
                Console.WriteLine("  [OK] 解压到 " + root);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [失败] 解压失败: " + ex.Message);
                return false;
            }
        }

        private static string CopySelfToUninstaller()
        {
            var self = Environment.ProcessPath;
            var dest = Path.Combine(_installRoot, "uninstall.exe");
            if (!string.Equals(self, dest, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(self, dest, true); } catch { /* ignore */ }
            }

            // 侧车 URL 配置一并复制（重装时仍可读取自定义地址）
            var side = Path.Combine(AppContext.BaseDirectory, "installer_url.txt");
            if (File.Exists(side))
            {
                try { File.Copy(side, Path.Combine(_installRoot, "installer_url.txt"), true); } catch { /* ignore */ }
            }

            return dest;
        }

        private static void CreateShortcuts(string uninstaller)
        {
            var clientExe = Path.Combine(_installRoot, "client", ClientExe);
            var serverExe = Path.Combine(_installRoot, "server", ServerExe);
            var clientDir = Path.Combine(_installRoot, "client");
            var serverDir = Path.Combine(_installRoot, "server");

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "OJ");
            try { Directory.CreateDirectory(startMenu); } catch { /* ignore */ }

            MakeLink(Path.Combine(desktop, "OJ客户端.lnk"), clientExe, clientDir, "OJ 在线评测客户端");
            MakeLink(Path.Combine(startMenu, "OJ客户端.lnk"), clientExe, clientDir, "OJ 在线评测客户端");
            MakeLink(Path.Combine(desktop, "OJ服务端.lnk"), serverExe, serverDir, "OJ 出题工作台");
            MakeLink(Path.Combine(startMenu, "OJ服务端.lnk"), serverExe, serverDir, "OJ 出题工作台");
            MakeLink(Path.Combine(desktop, "卸载 OJ.lnk"), uninstaller, _installRoot, "卸载 OJ", "/uninstall");
            MakeLink(Path.Combine(startMenu, "卸载 OJ.lnk"), uninstaller, _installRoot, "卸载 OJ", "/uninstall");

            Console.WriteLine("  [OK] 快捷方式已创建。");
        }

        private static void WriteRegistry(string uninstaller)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OJ");
                key.SetValue("DisplayName", "OJ 在线评测系统");
                key.SetValue("DisplayVersion", "1.0");
                key.SetValue("Publisher", "OJ");
                key.SetValue("InstallLocation", _installRoot);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("DisplayIcon", Path.Combine(_installRoot, "client", ClientExe));
                key.SetValue("NoModify", 1);
                key.SetValue("NoRepair", 1);
                Console.WriteLine("  [OK] 卸载信息已写入注册表。");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [!] 注册表写入失败（不影响安装）: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 卸载

        private static int DoUninstall()
        {
            Header("OJ 一键卸载");

            KillApps();

            // 1. 删除快捷方式
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "OJ");
            foreach (var p in new[]
            {
                Path.Combine(desktop, "OJ客户端.lnk"),
                Path.Combine(desktop, "OJ服务端.lnk"),
                Path.Combine(desktop, "卸载 OJ.lnk"),
                Path.Combine(startMenu, "OJ客户端.lnk"),
                Path.Combine(startMenu, "OJ服务端.lnk"),
                Path.Combine(startMenu, "卸载 OJ.lnk"),
            })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
            }
            try
            {
                if (Directory.Exists(startMenu) && !Directory.EnumerateFileSystemEntries(startMenu).Any())
                {
                    Directory.Delete(startMenu);
                }
            }
            catch { /* ignore */ }

            // 2. 删除注册表
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OJ", false);
            }
            catch { /* ignore */ }

            // 3. 删除安装目录
            if (Directory.Exists(_installRoot))
            {
                Console.WriteLine("  正在删除安装目录: " + _installRoot);
                var self = Environment.ProcessPath;
                var selfInside = self != null &&
                    self.StartsWith(_installRoot, StringComparison.OrdinalIgnoreCase);

                if (selfInside)
                {
                    ScheduleDelete(_installRoot);
                }
                else
                {
                    try { Directory.Delete(_installRoot, true); }
                    catch { ScheduleDelete(_installRoot); }
                }
            }
            else
            {
                Console.WriteLine("  未检测到安装目录，已清理快捷方式与注册表。");
            }

            Console.WriteLine("  [完成] 卸载成功。");
            Pause();
            return 0;
        }

        // ---------------------------------------------------------------- 工具

        private static void MakeLink(string lnkPath, string target, string workDir, string desc, string args = null)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                dynamic shell = Activator.CreateInstance(t);
                dynamic lnk = shell.CreateShortcut(lnkPath);
                lnk.TargetPath = target;
                lnk.WorkingDirectory = workDir;
                lnk.Description = desc;
                if (args != null) lnk.Arguments = args;
                if (File.Exists(target)) lnk.IconLocation = target + ",0";
                lnk.Save();
                Marshal.FinalReleaseComObject((object)lnk);
                Marshal.FinalReleaseComObject((object)shell);
            }
            catch { /* ignore */ }
        }

        private static void KillApps()
        {
            foreach (var name in new[] { "client", "Author" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); p.WaitForExit(2000); } catch { /* ignore */ }
                    }
                }
                catch { /* ignore */ }
            }
        }

        private static void ScheduleDelete(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping 127.0.0.1 -n 2 >nul & rmdir /s /q \"" + path + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            catch { /* ignore */ }
        }

        private static void Launch(string exe)
        {
            if (!File.Exists(exe))
            {
                Console.WriteLine("  [!] 未找到 " + exe);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [!] 启动失败: " + ex.Message);
            }
        }

        private static bool HasDesktopRuntime8()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
                if (key == null) return false;
                foreach (var v in key.GetSubKeyNames())
                {
                    if (v.StartsWith("8.", StringComparison.Ordinal)) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }

        private static string ResolveBaseUrl()
        {
            if (!string.IsNullOrWhiteSpace(_baseUrl)) return EnsureTrailingSlash(_baseUrl);

            var side = Path.Combine(AppContext.BaseDirectory, "installer_url.txt");
            if (File.Exists(side))
            {
                var t = File.ReadAllText(side).Trim();
                if (!string.IsNullOrWhiteSpace(t)) return EnsureTrailingSlash(t);
            }

            return EnsureTrailingSlash(DefaultBaseUrl);
        }

        private static string ResolveInstallRoot()
        {
            if (!string.IsNullOrWhiteSpace(_installRoot)) return _installRoot;

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

        private static string EnsureTrailingSlash(string url)
        {
            url = url.Trim();
            return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
        }

        private static string Readable(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB" };
            double b = bytes;
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return $"{b:0.#} {u[i]}";
        }

        private static char ReadKey()
        {
            try { return Console.ReadKey(true).KeyChar; }
            catch { return '\0'; }
        }

        private static void Pause()
        {
            if (_silent) return;
            Console.WriteLine();
            Console.Write("  按任意键退出...");
            try { Console.ReadKey(true); } catch { /* ignore */ }
        }

        private static void Header(string title)
        {
            Console.WriteLine("==============================================");
            Console.WriteLine("  " + title);
            Console.WriteLine("==============================================");
            Console.WriteLine();
        }

        private static void PrintHelp()
        {
            Console.WriteLine("用法: OJInstaller [选项]");
            Console.WriteLine();
            Console.WriteLine("  无参数            一键安装（从服务器下载并安装客户端+服务端）");
            Console.WriteLine("  /uninstall        一键卸载");
            Console.WriteLine("  /url=<地址>       指定安装包下载地址前缀（以 / 结尾）");
            Console.WriteLine("  /pkg=<文件名>     指定安装包文件名（默认 oj-package.zip）");
            Console.WriteLine("  /dir=<目录>       指定安装目录（默认 %LocalAppData%\\OJ）");
            Console.WriteLine("  /run              安装完成后自动启动客户端");
            Console.WriteLine("  /silent           静默模式（不等待按键）");
        }
    }
}
