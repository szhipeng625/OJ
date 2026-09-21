using System.IO;
using System.Windows;
using author.Business.Services;
using author.DataAccess;
using author.Presentation.Helpers;
using author.Presentation.Views;

namespace author;

public partial class App : Application
{
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

            // 客户端题目目录（MySQL 初始化需要，dev 上溯 5 级到仓库根；发布环境在 exe 上级）
            string[] serverCandidates =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "server", "problems")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "server", "problems")),
                Path.GetFullPath(Path.Combine(baseDir, "server", "problems")),
            };
            string serverProblemDir = serverCandidates.FirstOrDefault(Directory.Exists) ?? serverCandidates[0];

            // 服务端自有题库 / 编译临时目录（dev：Author\problems、Author\temp）
            string[] authorCandidates =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "problems")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "problems")),
                Path.GetFullPath(Path.Combine(baseDir, "problems")),
            };
            string authorProblemDir = authorCandidates.FirstOrDefault(Directory.Exists) ?? authorCandidates[0];

            string[] tempCandidates =
            {
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "temp")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "temp")),
                Path.GetFullPath(Path.Combine(baseDir, "temp")),
            };
            string tempRoot = tempCandidates.FirstOrDefault(Directory.Exists) ?? tempCandidates[0];

            // ---- 组合根：DAL → BLL ----
            var authClient = new AuthClient();
                        var authorClient = new AuthorClient();
            var contestClient = new ContestClient();

            authorClient.Init(authorProblemDir, tempRoot);   // authorcore：题库根 + temp 目录

            var build = new BuildService();
            var auth = new AuthService(authClient);
            var problems = new ProblemService(authorClient, build);
            var generators = new GeneratorService(authorClient, build);
            var contests = new ContestService(contestClient, build);
            var workbench = new Workbench
            {
                Auth = auth,
                Problems = problems,
                Generators = generators,
                Contests = contests,
                Build = build,
            };

            // ---- MySQL 认证（服务端强制要求） ----
            if (!await auth.InitAsync(serverProblemDir))
            {
                MessageBox.Show("未配置 MySQL 连接（缺少 mysql_config.json 或连不上数据库），服务端无法启动。\n请参考 docs/MYSQL.md。",
                    "OJ 服务端", MessageBoxButton.OK, MessageBoxImage.Error);
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
