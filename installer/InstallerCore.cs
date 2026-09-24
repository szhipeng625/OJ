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
    /// 安装 / 卸载核心逻辑（与界面无关，进度与日志通过回调输出）。
    /// 安装：下载 oj-package.zip（client/ + server/ + shared/，公共依赖已去重）解压到安装目录，
    ///       创建快捷方式、写入卸载注册表项、复制自身为 uninstall.exe。
    /// </summary>
    public class InstallerCore
    {
        private const string DefaultPackageName = "oj-package.zip";
        private const string ClientExe = "client.exe";
        private const string ServerExe = "Author.exe";

        public string BaseUrl { get; set; }
        public string InstallRoot { get; set; }
        public string PackageName { get; set; } = DefaultPackageName;
        public bool CreateDesktopShortcuts { get; set; } = true;

        private static string DefaultBaseUrl =>
            typeof(InstallerCore).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "InstallerBaseUrl")?.Value
            ?? "http://YOUR-SERVER/oj/";

        // ---------------------------------------------------------------- 安装

        public async Task<bool> InstallAsync(Action<string> log, Action<long, long?> progress, bool runAfter)
        {
            var root = ResolveInstallRoot();
            var baseUrl = ResolveBaseUrl();

            log?.Invoke("安装目录: " + root);

            if (Directory.Exists(root))
            {
                log?.Invoke("检测到已安装，将覆盖安装。");
                KillApps();
            }

            var zip = Path.Combine(Path.GetTempPath(), PackageName);
            var url = baseUrl + PackageName;

            log?.Invoke("开始下载安装包...");
            if (!await DownloadAsync(url, zip, progress, log)) return false;

            await TryVerifyAsync(url + ".sha256", zip, log);

            log?.Invoke("正在解压...");
            if (!Extract(zip, root, log)) return false;

            var uninstaller = CopySelfToUninstaller(root);
            CreateShortcuts(root, uninstaller, log);
            WriteRegistry(root, uninstaller, log);

            var cExe = Path.Combine(root, "client", ClientExe);
            var sExe = Path.Combine(root, "server", ServerExe);
            if (!File.Exists(cExe) || !File.Exists(sExe))
                log?.Invoke("[警告] 未找到 client/client.exe 或 server/Author.exe，请确认安装包内容。");

            log?.Invoke("安装成功。桌面 / 开始菜单已创建快捷方式。");

            if (runAfter) Launch(cExe);
            return true;
        }

        // ---------------------------------------------------------------- 卸载

        public bool Uninstall(Action<string> log)
        {
            var root = ResolveInstallRoot();

            KillApps();

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ACMDOG");
            foreach (var p in new[]
            {
                Path.Combine(desktop, "ACMDOG客户端.lnk"),
                Path.Combine(desktop, "ACMDOG服务端.lnk"),
                Path.Combine(desktop, "卸载 ACMDOG.lnk"),
                Path.Combine(startMenu, "ACMDOG客户端.lnk"),
                Path.Combine(startMenu, "ACMDOG服务端.lnk"),
                Path.Combine(startMenu, "卸载 ACMDOG.lnk"),
            })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
            }
            try
            {
                if (Directory.Exists(startMenu) && !Directory.EnumerateFileSystemEntries(startMenu).Any())
                    Directory.Delete(startMenu);
            }
            catch { /* ignore */ }

            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OJ", false);
            }
            catch { /* ignore */ }

            if (Directory.Exists(root))
            {
                log?.Invoke("正在删除安装目录: " + root);
                var self = Environment.ProcessPath;
                var selfInside = self != null &&
                    self.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                if (selfInside)
                {
                    ScheduleDelete(root);
                }
                else
                {
                    try { Directory.Delete(root, true); }
                    catch { ScheduleDelete(root); }
                }
            }
            else
            {
                log?.Invoke("未检测到安装目录，已清理快捷方式与注册表。");
            }

            log?.Invoke("卸载完成。");
            return true;
        }

        // ---------------------------------------------------------------- 工具

        private async Task<bool> DownloadAsync(string url, string dest, Action<long, long?> progress, Action<string> log)
        {
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
                    progress?.Invoke(got, total > 0 ? (long?)total : null);
                }

                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("[失败] 下载失败: " + ex.Message);
                log?.Invoke("请检查网络连接后重试。");
                return false;
            }
        }

        private async Task TryVerifyAsync(string url, string file, Action<string> log)
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

                log?.Invoke(string.Equals(actual, hash.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
                    ? "[OK] SHA256 校验通过。"
                    : "[!] SHA256 校验不一致，但继续安装。");
            }
            catch
            {
                // 校验文件可选，缺失/失败不阻断
            }
        }

        private static bool Extract(string zip, string root, Action<string> log)
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
                log?.Invoke("[OK] 解压完成。");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("[失败] 解压失败: " + ex.Message);
                return false;
            }
        }

        private static string CopySelfToUninstaller(string root)
        {
            var self = Environment.ProcessPath;
            var dest = Path.Combine(root, "uninstall.exe");
            if (!string.Equals(self, dest, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(self, dest, true); } catch { /* ignore */ }
            }

            // 侧车 URL 配置一并复制（重装时仍可读取自定义地址）
            var side = Path.Combine(AppContext.BaseDirectory, "installer_url.txt");
            if (File.Exists(side))
            {
                try { File.Copy(side, Path.Combine(root, "installer_url.txt"), true); } catch { /* ignore */ }
            }

            return dest;
        }

        private void CreateShortcuts(string root, string uninstaller, Action<string> log)
        {
            var clientExe = Path.Combine(root, "client", ClientExe);
            var serverExe = Path.Combine(root, "server", ServerExe);
            var clientDir = Path.Combine(root, "client");
            var serverDir = Path.Combine(root, "server");

            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ACMDOG");
            try { Directory.CreateDirectory(startMenu); } catch { /* ignore */ }

            MakeLink(Path.Combine(startMenu, "ACMDOG客户端.lnk"), clientExe, clientDir, "ACMDOG 客户端");
            MakeLink(Path.Combine(startMenu, "ACMDOG服务端.lnk"), serverExe, serverDir, "ACMDOG 出题工作台");
            MakeLink(Path.Combine(startMenu, "卸载 ACMDOG.lnk"), uninstaller, root, "卸载 ACMDOG", "/uninstall");

            if (CreateDesktopShortcuts)
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                MakeLink(Path.Combine(desktop, "ACMDOG客户端.lnk"), clientExe, clientDir, "ACMDOG 客户端");
                MakeLink(Path.Combine(desktop, "ACMDOG服务端.lnk"), serverExe, serverDir, "ACMDOG 出题工作台");
                MakeLink(Path.Combine(desktop, "卸载 ACMDOG.lnk"), uninstaller, root, "卸载 ACMDOG", "/uninstall");
            }

            log?.Invoke("[OK] 快捷方式已创建。");
        }

        private static void WriteRegistry(string root, string uninstaller, Action<string> log)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OJ");
                key.SetValue("DisplayName", "ACMDOG");
                key.SetValue("DisplayVersion", "1.0");
                key.SetValue("Publisher", "ACMDOG");
                key.SetValue("InstallLocation", root);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("DisplayIcon", Path.Combine(root, "client", ClientExe));
                key.SetValue("NoModify", 1);
                key.SetValue("NoRepair", 1);
                log?.Invoke("[OK] 卸载信息已写入注册表。");
            }
            catch (Exception ex)
            {
                log?.Invoke("[!] 注册表写入失败（不影响安装）: " + ex.Message);
            }
        }

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
            if (!File.Exists(exe)) return;
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
        }

        public static bool HasDesktopRuntime8()
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

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }

        public string ResolveBaseUrl()
        {
            if (!string.IsNullOrWhiteSpace(BaseUrl)) return EnsureTrailingSlash(BaseUrl);

            var side = Path.Combine(AppContext.BaseDirectory, "installer_url.txt");
            if (File.Exists(side))
            {
                var t = File.ReadAllText(side).Trim();
                if (!string.IsNullOrWhiteSpace(t)) return EnsureTrailingSlash(t);
            }

            return EnsureTrailingSlash(DefaultBaseUrl);
        }

        public string ResolveInstallRoot()
        {
            if (!string.IsNullOrWhiteSpace(InstallRoot)) return InstallRoot;

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
    }
}
