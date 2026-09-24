using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Threading;
using client.Business.Services;
using client.DataAccess;
using client.DataAccess.Models;
using client.Presentation.Views;

namespace client;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "startup.log");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    // 分层目录：UI 程序集放 ui/，原生 DLL 放 native/；目录不存在时自动回退到 exe 根目录。
    static App()
    {
        string baseDir = AppContext.BaseDirectory;
        string nativeDir = Path.Combine(baseDir, "native");
        string uiDir = Path.Combine(baseDir, "ui");

        // 原生 DLL（ojcore/OpenSSL）统一从 native/ 加载，含其传递依赖
        if (Directory.Exists(nativeDir))
            SetDllDirectory(nativeDir);

        // 第三方托管程序集（HandyControl/AvalonEdit/Markdig）从 ui/ 加载
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name is null) return null;
            string p = Path.Combine(uiDir, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) =>
        {
            Log("UI 异常: " + ev.Exception);
            MessageBox.Show(ev.Exception.ToString(), "启动异常");
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            Log("未处理异常: " + ev.ExceptionObject);

        try
        {
            File.WriteAllText(LogPath, "");
            Log("OnStartup 开始");

            // ---- 组合根：DAL → BLL ----
            var api = new ApiClient();                                   // 数据访问层
            var auth = new AuthService(api);                             // 业务逻辑层
            var judge = new JudgeService(api);
            var identity = new LocalIdentityService();
            Log("服务初始化完成, MySqlEnabled=" + api.MySqlEnabled);

            // ---- 登录（仅 MySQL 模式）----
            if (api.MySqlEnabled)
            {
                LoginResult? me = await auth.RestoreSessionAsync();
                if (me is not { Ok: true })
                {
                    Log("弹登录窗");
                    var login = new LoginWindow(auth);
                    bool? dr = login.ShowDialog();
                    Log("登录窗返回: " + dr);
                    if (dr != true) { Shutdown(); return; }
                    auth.SaveSession(login.Session!);
                    me = login.Session;
                }
                Log("登录成功: " + me?.Username);

                // 登录完成后再同步题目/比赛（避免阻塞登录窗口弹出）
                Log("同步题目开始");
                await Task.Run(() => api.SyncProblems());
                Log("同步题目完成");
            }

            // ---- UI 层 ----
            Log("创建 MainWindow");
            var win = new MainWindow(judge, auth, identity);
            MainWindow = win;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            win.Show();
            Log("MainWindow 已显示");
        }
        catch (Exception ex)
        {
            Log("OnStartup 异常: " + ex);
            MessageBox.Show(ex.ToString(), "启动失败");
            Shutdown();
        }
    }
}
