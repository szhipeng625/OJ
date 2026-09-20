using System.IO;
using System.Windows;
using author.Services;

namespace author;

public partial class App : Application
{
    public static UserAuth.Session? CurrentUser { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 题目目录候选：开发环境上溯 5 级到仓库根，发布环境上溯 1 级
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "server", "problems")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "server", "problems")),
            Path.GetFullPath(Path.Combine(baseDir, "server", "problems")),
        };
        string problemDir = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];

        if (!UserAuth.Init(problemDir))
        {
            MessageBox.Show("未配置 MySQL 连接（缺少 mysql_config.json 或连不上数据库），服务端无法启动。\n请参考 docs/MYSQL.md。",
                "OJ 服务端", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // 尝试恢复已登录会话
        var tokenPath = Path.Combine(AppContext.BaseDirectory, "auth", "session.txt");
        if (File.Exists(tokenPath))
        {
            var token = File.ReadAllText(tokenPath).Trim();
            var me = UserAuth.Whoami(token);
            if (me.Ok && (me.Role == "admin" || me.Role == "author"))
            {
                CurrentUser = me;
                ShowMain();
                return;
            }
        }

        var login = new LoginWindow();
        if (login.ShowDialog() == true && login.Session is { } s)
        {
            CurrentUser = s;
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "auth"));
            File.WriteAllText(tokenPath, s.Token);
            ShowMain();
        }
        else
        {
            Shutdown();
        }
    }

    private void ShowMain()
    {
        var win = new MainWindow();
        MainWindow = win;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        win.Show();
    }
}
