using System.IO;
using author.Business.Services;
using author.DataAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace author.CodexMcp;

/// <summary>
/// 把出题端挂成 MCP 服务：`Author.exe --mcp` 时走这条路径，不起任何窗口，
/// 直接组一套业务层（复用既有 AuthorClient / BuildService / ProblemService），
/// 然后以 stdio 承载 MCP 协议。
///
/// 两条纪律：
///   · stdout 只走 MCP 的 JSON-RPC，日志一律进 stderr —— 否则协议会被污染；
///   · 工具实现与面板共用 <see cref="AuthorOjToolHost"/>，MCP 与面板行为一致。
/// </summary>
public static class CodexMcpHost
{
    /// <summary>命令行开关：出现即进入 MCP 模式。</summary>
    public const string Flag = "--mcp";

    public static bool IsMcpMode(string[] args) =>
        args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>组装业务层 + 启动 stdio MCP 服务（阻塞直到客户端断开）。</summary>
    public static async Task RunAsync()
    {
        // 注意：不要设置 Console.OutputEncoding —— 作为 MCP 子进程启动时没有控制台句柄，
        // 赋值会抛 IOException(句柄无效)。MCP 传输层自己按 UTF-8 写 stdout。

        var workbench = BuildWorkbench();

        var host = new AuthorOjToolHost(workbench);
        OjMcpTools.Attach(host);

        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        // 关键：日志全部改道 stderr。stdout 留给协议本身。
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<OjMcpTools>();

        await builder.Build().RunAsync();
    }

    /// <summary>
    /// 复用出题端的组合方式组装 Workbench。MCP 模式不做交互式登录 ——
    /// 题库读写只依赖中间件连接，不依赖登录态。
    /// </summary>
    private static Workbench BuildWorkbench()
    {
        string authorProblemDir = OjPaths.AuthorProblemDir;
        string serverProblemDir = OjPaths.ServerProblemDir;
        string tempRoot = OjPaths.TempDir;
        string dataDir = OjPaths.DataDir;

        foreach (var d in new[] { authorProblemDir, serverProblemDir, tempRoot, dataDir })
        {
            try { Directory.CreateDirectory(d); } catch { /* 建目录失败不阻塞 */ }
        }

        var authClient = new AuthClient();
        var authorClient = new AuthorClient();
        var contestClient = new ContestClient();
        authorClient.Init(authorProblemDir, tempRoot);

        // 建立中间件连接（题库读写的唯一入口；MCP 模式不做交互式登录）
        authClient.Init(serverProblemDir, dataDir);

        var build = new BuildService();
        return new Workbench
        {
            Auth = new AuthService(authClient),
            Problems = new ProblemService(authorClient, build),
            Generators = new GeneratorService(new GeneratorClient(), authorClient, build),
            Contests = new ContestService(contestClient, build),
            Build = build,
            ServerRoot = OjPaths.ServerRoot,
            ServerProblemDir = serverProblemDir,
        };
    }
}
