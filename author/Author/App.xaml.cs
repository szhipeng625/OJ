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

    // 分层目录：UI 程序集放 ui/，原生 DLL 放 native/；目录不存在时自动回退到 exe 根目录。
    static App()
    {
        string baseDir = AppContext.BaseDirectory;
        string nativeDir = Path.Combine(baseDir, "native");
        string uiDir = Path.Combine(baseDir, "ui");

        // 原生 DLL（ojcore/authorcore/OpenSSL）统一从 native/ 加载，含其传递依赖
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
            string baseDir = AppContext.BaseDirectory;

            // 全部数据目录相对 exe 目录（可移植），自动创建
            string authorProblemDir = Path.Combine(baseDir, "problems");       // 出题端自有题库
            string serverRoot = Path.Combine(baseDir, "server");               // 客户端服务端根目录
            string serverProblemDir = Path.Combine(serverRoot, "problems");    // 题目发布目标（本地兜底）
            string testDataDir = Path.Combine(baseDir, "testdata");            // 测试数据目录
            string tempRoot = Path.Combine(baseDir, "temp");                   // 编译临时目录
            string dataDir = Path.Combine(baseDir, "ojdata");                  // 本地提交记录
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
                MessageBox.Show("未配置 MySQL 连接（缺少 mysql_config.json 或连不上数据库），服务端无法启动。\n请参考 docs/MYSQL.md。",
                    "ACMDOG 服务端", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            string tokenPath = Path.Combine(baseDir, "auth", "session.txt");
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
