using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace OJInstaller
{
    /// <summary>
    /// 安装/卸载核心逻辑（与 UI 无关）：
    /// 按需从服务器下载 oj-client.zip（客户端）/ oj-server.zip（出题端，自包含）并解压，
    /// 创建快捷方式与卸载注册表项，复制自身为 uninstall.exe。
    /// 日志与下载进度通过回调抛给 UI。
    /// </summary>
    public sealed class InstallerCore
    {
        public const string ClientPackageName = "oj-client.zip";
        public const string ServerPackageName = "oj-server.zip";
        public const string ClientExe = "client.exe";
        public const string ServerExe = "Author.exe";

        /// <summary>安装包下载地址前缀（以 / 结尾）。</summary>
        public string BaseUrl = "http://YOUR-SERVER/oj/";

        /// <summary>安装根目录。</summary>
        public string InstallRoot = "";

        public bool InstallClient = true;
        public bool InstallServer = true;

        /// <summary>是否创建桌面快捷方式（开始菜单快捷方式始终创建）。</summary>
        public bool CreateDesktopShortcuts = true;

        /// <summary>日志回调（可能来自后台线程，UI 需自行封送到界面线程）。</summary>
        public Action<string> Log;

        /// <summary>下载进度回调：bytes received / total（total=0 表示未知）。</summary>
        public Action<long, long> Progress;

        public void LogLine(string msg) => Log?.Invoke(msg);

        // ---------------------------------------------------------------- 安装

        public async Task<bool> InstallAsync()
        {
            LogLine("开始安装 ACMDOG ...");
            LogLine("安装目录: " + InstallRoot);
            LogLine("下载地址: " + BaseUrl);
            LogLine("");

            if (Directory.Exists(InstallRoot))
            {
                LogLine("[!] 检测到已安装，将覆盖所选组件。");
                KillApps();
            }

            // 按需下载 + 解压（只下载需要的包，DLL 等运行文件都包含在包内）
            if (InstallClient && !await DownloadAndExtractAsync(ClientPackageName, "client"))
                return false;
            if (InstallServer && !await DownloadAndExtractAsync(ServerPackageName, "server"))
                return false;

            // 复制自身为卸载器
            var uninstaller = CopySelfToUninstaller();

            // 快捷方式 + 注册表
            CreateShortcuts(uninstaller);
            WriteRegistry(uninstaller);

            // 完整性检查
            var cExe = Path.Combine(InstallRoot, "client", ClientExe);
            var sExe = Path.Combine(InstallRoot, "server", ServerExe);
            if (InstallClient && !File.Exists(cExe))
                LogLine("[!] 警告：未找到 client/client.exe。");
            if (InstallServer && !File.Exists(sExe))
                LogLine("[!] 警告：未找到 server/Author.exe。");

            // .NET 8 桌面运行时检查（客户端/服务端为框架依赖，运行需要）
            if (!HasDesktopRuntime8())
            {
                LogLine("");
                LogLine("[!] 未检测到 .NET 8 桌面运行时（运行客户端/服务端需要）。");
                LogLine("    下载地址: https://dotnet.microsoft.com/download/dotnet/8.0");
            }

            LogLine("");
            LogLine("[完成] 安装成功。");
            return true;
        }

        private async Task<bool> DownloadAndExtractAsync(string packageName, string innerDir)
        {
            var zip = Path.Combine(Path.GetTempPath(), packageName);
            var url = BaseUrl + packageName;
            if (!await DownloadAsync(url, zip))
                return false;

            // 可选 SHA256 校验
            await TryVerifyAsync(url + ".sha256", zip);

            try
            {
                var sub = Path.Combine(InstallRoot, innerDir);
                if (Directory.Exists(sub))
                {
                    try { Directory.Delete(sub, true); } catch { /* ignore */ }
                }
                Directory.CreateDirectory(InstallRoot);
                ZipFile.ExtractToDirectory(zip, InstallRoot, true);
                LogLine("[OK] 解压 " + packageName + " 到 " + InstallRoot);
                return true;
            }
            catch (Exception ex)
            {
                LogLine("[失败] 解压失败: " + ex.Message);
                return false;
            }
        }

        private async Task<bool> DownloadAsync(string url, string dest)
        {
            LogLine("下载: " + url);
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
                    Progress?.Invoke(got, total);
                }

                LogLine("[OK] 下载完成 (" + Readable(got) + ")。");
                return true;
            }
            catch (Exception ex)
            {
                LogLine("[失败] 下载失败: " + ex.Message);
                LogLine("       请确认浏览器能打开: " + url);
                return false;
            }
        }

        private async Task TryVerifyAsync(string url, string file)
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

                LogLine(string.Equals(actual, hash.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
                    ? "[OK] SHA256 校验通过。"
                    : "[!] SHA256 校验不一致，但继续安装。");
            }
            catch
            {
                // 校验文件可选，缺失/失败不阻断
            }
        }

        private string CopySelfToUninstaller()
        {
            var self = Environment.ProcessPath;
            var dest = Path.Combine(InstallRoot, "uninstall.exe");
            if (!string.Equals(self, dest, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(self, dest, true); } catch { /* ignore */ }
            }

            // 侧车 URL 配置一并复制（重装时仍可读取自定义地址）
            var side = Path.Combine(AppContext.BaseDirectory, "installer_url.txt");
            if (File.Exists(side))
            {
                try { File.Copy(side, Path.Combine(InstallRoot, "installer_url.txt"), true); } catch { /* ignore */ }
            }

            return dest;
        }

        private void CreateShortcuts(string uninstaller)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ACMDOG");
            try { Directory.CreateDirectory(startMenu); } catch { /* ignore */ }

            if (InstallClient)
            {
                var clientExe = Path.Combine(InstallRoot, "client", ClientExe);
                var clientDir = Path.Combine(InstallRoot, "client");
                if (CreateDesktopShortcuts)
                    MakeLink(Path.Combine(desktop, "ACMDOG客户端.lnk"), clientExe, clientDir, "ACMDOG 客户端");
                MakeLink(Path.Combine(startMenu, "ACMDOG客户端.lnk"), clientExe, clientDir, "ACMDOG 客户端");
            }
            if (InstallServer)
            {
                var serverExe = Path.Combine(InstallRoot, "server", ServerExe);
                var serverDir = Path.Combine(InstallRoot, "server");
                if (CreateDesktopShortcuts)
                    MakeLink(Path.Combine(desktop, "ACMDOG服务端.lnk"), serverExe, serverDir, "ACMDOG 出题工作台");
                MakeLink(Path.Combine(startMenu, "ACMDOG服务端.lnk"), serverExe, serverDir, "ACMDOG 出题工作台");
            }
            if (CreateDesktopShortcuts)
                MakeLink(Path.Combine(desktop, "卸载 ACMDOG.lnk"), uninstaller, InstallRoot, "卸载 ACMDOG", "/uninstall");
            MakeLink(Path.Combine(startMenu, "卸载 ACMDOG.lnk"), uninstaller, InstallRoot, "卸载 ACMDOG", "/uninstall");

            LogLine("[OK] 快捷方式已创建。");
        }

        private void WriteRegistry(string uninstaller)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OJ");
                key.SetValue("DisplayName", "ACMDOG");
                key.SetValue("DisplayVersion", "1.0");
                key.SetValue("Publisher", "ACMDOG");
                key.SetValue("InstallLocation", InstallRoot);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("DisplayIcon", InstallClient
                    ? Path.Combine(InstallRoot, "client", ClientExe)
                    : Path.Combine(InstallRoot, "server", ServerExe));
                key.SetValue("NoModify", 1);
                key.SetValue("NoRepair", 1);
                LogLine("[OK] 卸载信息已写入注册表。");
            }
            catch (Exception ex)
            {
                LogLine("[!] 注册表写入失败（不影响安装）: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 卸载

        public bool Uninstall()
        {
            LogLine("开始卸载 ACMDOG ...");

            KillApps();

            // 1. 删除快捷方式
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
            if (Directory.Exists(InstallRoot))
            {
                LogLine("正在删除安装目录: " + InstallRoot);
                var self = Environment.ProcessPath;
                var selfInside = self != null &&
                    self.StartsWith(InstallRoot, StringComparison.OrdinalIgnoreCase);

                if (selfInside)
                {
                    ScheduleDelete(InstallRoot);
                }
                else
                {
                    try { Directory.Delete(InstallRoot, true); }
                    catch { ScheduleDelete(InstallRoot); }
                }
            }
            else
            {
                LogLine("未检测到安装目录，已清理快捷方式与注册表。");
            }

            LogLine("[完成] 卸载完成。");
            return true;
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

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }

        public static string EnsureTrailingSlash(string url)
        {
            url = url.Trim();
            return url.EndsWith("/", StringComparison.Ordinal) ? url : url + "/";
        }

        public static string Readable(long bytes)
        {
            string[] u = { "B", "KB", "MB", "GB" };
            double b = bytes;
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return $"{b:0.#} {u[i]}";
        }
    }
}
