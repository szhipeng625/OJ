using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Windows;
using author.Business.Services;
using author.DataAccess;
using author.Presentation.Helpers;
using author.Presentation.Views;

namespace author;

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

        // 3. 原生 DLL（ojcore/authorcore/OpenSSL）统一从 NativeDir 加载，含其传递依赖；
        // 安装版公共依赖放 ..\shared\native（客户端/服务端共用，避免重复），开发版仍在 exe 同目录 native/
        string? nativeDir = OjPaths.NativeDir;
        if (nativeDir is not null)
        {
            SetDllDirectory(nativeDir);
            // 预加载两个原生 DLL（文件名来自 config.xml），DllImport 同名即命中；
            // 加载失败不阻塞启动（后续 DllImport 会按目录再解析）。
            foreach (var dll in new[] { OjPaths.OjCoreDll, OjPaths.AuthorCoreDll })
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) =>
        {
            MessageBox.Show(ev.Exception.ToString(), "未处理异常");
            ev.Handled = true;
        };

        try
        {
            // 全部数据目录来自全局路径注册表（OjPaths，由 config.xml 解析），自动创建
            string authorProblemDir = OjPaths.AuthorProblemDir;    // 出题端自有题库
            string serverRoot = OjPaths.ServerRoot;                // 客户端服务端根目录
            string serverProblemDir = OjPaths.ServerProblemDir;   // 题目发布目标（本地兜底）
            string testDataDir = OjPaths.TestDataDir;              // 测试数据目录
            string tempRoot = OjPaths.TempDir;                     // 编译临时目录
            string dataDir = OjPaths.DataDir;                      // 本地提交记录
            foreach (var d in new[] { authorProblemDir, serverProblemDir, testDataDir, tempRoot, dataDir })
                try { Directory.CreateDirectory(d); } catch { /* 创建目录失败不阻塞启动 */ }

            // ---- 组合根：DAL → BLL ----
            var authClient = new AuthClient();
                        var authorClient = new AuthorClient();
            var contestClient = new ContestClient();

            authorClient.Init(authorProblemDir, tempRoot);   // authorcore：题库根 + temp 目录

            var build = new BuildService();
            var auth = new AuthService(authClient);
            var problems = new ProblemService(authorClient, build);
            var generatorClient = new GeneratorClient();
            var generators = new GeneratorService(generatorClient, authorClient, build);
            var contests = new ContestService(contestClient, build);
            var workbench = new Workbench
            {
                Auth = auth,
                Problems = problems,
                Generators = generators,
                Contests = contests,
                Build = build,
                ServerRoot = serverRoot,
                ServerProblemDir = serverProblemDir,
            };

            // ---- MySQL 认证（服务端强制要求） ----
            if (!await auth.InitAsync(serverProblemDir, dataDir))
            {
                MessageBox.Show("未配置中间件地址（config.xml 的 middlewareUrl 为空或连不上服务器），服务端无法启动。\n请参考 docs/MYSQL.md。",
                    "ACMDOG 服务端", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            string tokenPath = OjPaths.SessionFile;
            var me = await auth.RestoreSessionAsync(tokenPath);
            if (me is null)
            {
                var login = new LoginWindow(auth);
                if (login.ShowDialog() == true && login.Session is { } s)
                {
                    auth.SetCurrent(s);
                    auth.SaveSession(tokenPath, s.Token);
                }
                else
                {
                    Shutdown();
                    return;
                }
            }

            // ---- UI：启动器 + 多窗口管理器 ----
            var manager = new WorkspaceManager(workbench);
            var main = new MainWindow(workbench, manager);
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "启动失败");
            Shutdown();
        }
    }
}
