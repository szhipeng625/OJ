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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    // 启动配置（config.xml）最先加载 → 全局路径注册表（OjPaths）→ 预加载原生 DLL。
    // 原生 DLL 放 native/，第三方托管程序集放 ui/；目录不存在时自动回退到 exe 根目录。
    static App()
    {
        AppConfig.Load();       // 1. 读取 config.xml（缺失自动生成模板）
        OjPaths.Initialize();   // 2. 解析全部路径到全局类

        // 3. 原生 DLL（ojcore/OpenSSL）统一从 NativeDir 加载，含其传递依赖；
        // 安装版公共依赖放 ..\shared\native（客户端/服务端共用，避免重复），开发版仍在 exe 同目录 native/
        string? nativeDir = OjPaths.NativeDir;
        if (nativeDir is not null)
        {
            SetDllDirectory(nativeDir);
            // 预加载原生 DLL（文件名同样来自 config.xml），DllImport 同名即命中；
            // 加载失败不阻塞启动（后续 DllImport 会按目录再解析）。
            foreach (var dll in new[] { OjPaths.OjCoreDll })
            {
                string p = Path.Combine(nativeDir, dll);
                if (File.Exists(p))
                {
                    try { NativeLibrary.Load(p); } catch { /* 预加载失败不阻塞 */ }
                }
            }
        }

        // 4. 第三方托管程序集（HandyControl/AvalonEdit/Markdig）从 AssemblyDir 加载，同样回退到 ..\shared\ui
        string? uiDir = OjPaths.AssemblyDir;
        if (uiDir is not null)
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                if (name.Name is null) return null;
                string p = Path.Combine(uiDir, name.Name + ".dll");
                return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
            };
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(OjPaths.LogPath, $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
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
            File.WriteAllText(OjPaths.LogPath, "");
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
