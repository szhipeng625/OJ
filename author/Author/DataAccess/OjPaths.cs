using System;
using System.IO;

namespace author.DataAccess;

/// <summary>
/// 全局路径注册表：进程内所有需要预处理的路径（DLL 加载目录、题库、数据、
/// 临时、发布、日志、MySQL 配置等）的唯一权威来源。
/// 初始化流程：App 静态构造函数先 AppConfig.Load()（读 config.xml），再
/// <see cref="Initialize"/> 把解析后的绝对路径一次性填充到这里；此后任何
/// 业务代码取路径一律走本类，禁止在代码中硬编码或自行拼接路径。
/// 前端启动时用这里的路径调用后端 C++ 完成初始化（oj_init / ac_init）。
/// </summary>
public static class OjPaths
{
    /// <summary>exe 所在目录（所有相对路径的基准）。</summary>
    public static string BaseDir { get; private set; } = "";

    // ---------- DLL 加载 ----------
    /// <summary>原生 DLL 加载目录（SetDllDirectory 目标，含传递依赖）。null = 未找到。</summary>
    public static string? NativeDir { get; private set; }

    /// <summary>第三方托管程序集目录（AssemblyLoadContext 解析目录）。null = 未找到。</summary>
    public static string? AssemblyDir { get; private set; }

    /// <summary>ACMDOGCodex 专用程序集目录（Claude SDK / MCP / Microsoft.Extensions）。</summary>
    public static string? CodexDir { get; private set; }

    /// <summary>判题核心 DLL 文件名。</summary>
    public static string OjCoreDll { get; private set; } = "ojcore.dll";

    /// <summary>出题核心 DLL 文件名。</summary>
    public static string AuthorCoreDll { get; private set; } = "authorcore.dll";

    /// <summary>ojcore 完整路径（用于启动时预加载）。</summary>
    public static string OjCoreDllPath => NativeDir is null ? OjCoreDll : Path.Combine(NativeDir, OjCoreDll);

    /// <summary>authorcore 完整路径（用于启动时预加载）。</summary>
    public static string AuthorCoreDllPath => NativeDir is null ? AuthorCoreDll : Path.Combine(NativeDir, AuthorCoreDll);

    // ---------- 客户端 ----------
    /// <summary>客户端题库目录。</summary>
    public static string ProblemDir { get; private set; } = "";

    /// <summary>本地提交记录目录。</summary>
    public static string DataDir { get; private set; } = "";

    /// <summary>启动日志文件完整路径。</summary>
    public static string LogPath { get; private set; } = "";

    /// <summary>模板代码目录（gen.template.cpp / std.template.cpp / itf.template.cpp）。</summary>
    public static string TemplatesDir { get; private set; } = "";

    // ---------- 出题端 ----------
    /// <summary>出题端自有题库目录。</summary>
    public static string AuthorProblemDir { get; private set; } = "";

    /// <summary>客户端/服务端根目录（发布目标所在）。</summary>
    public static string ServerRoot { get; private set; } = "";

    /// <summary>题目发布目标目录（本地兜底）。</summary>
    public static string ServerProblemDir { get; private set; } = "";

    /// <summary>测试数据目录。</summary>
    public static string TestDataDir { get; private set; } = "";

    /// <summary>编译临时目录。</summary>
    public static string TempDir { get; private set; } = "";

    /// <summary>登录会话文件完整路径。</summary>
    public static string SessionFile { get; private set; } = "";

    /// <summary>读取模板目录下的模板代码文件；文件不存在返回 null（调用方回退内置模板）。</summary>
    public static string? TemplateContent(string fileName)
    {
        try
        {
            string p = Path.Combine(TemplatesDir, fileName);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 由 AppConfig.Current 一次性解析填充全部路径。
    /// 必须在 AppConfig.Load() 之后调用（App 静态构造函数中最先执行）。
    /// </summary>
    public static void Initialize()
    {
        var cfg = AppConfig.Current;
        BaseDir = AppContext.BaseDirectory;
        NativeDir = cfg.FirstExistingDir(cfg.DllDirs);
        AssemblyDir = cfg.FirstExistingDir(cfg.AssemblyDirs);
        CodexDir = cfg.FirstExistingDir(cfg.CodexDirs);
        OjCoreDll = cfg.OjCoreDll;
        AuthorCoreDll = cfg.AuthorCoreDll;
        ProblemDir = cfg.Resolve(cfg.ProblemsDir);
        DataDir = cfg.Resolve(cfg.DataDir);
        LogPath = cfg.Resolve(cfg.StartupLogFile);
        TemplatesDir = cfg.Resolve(cfg.TemplatesDir);
        AuthorProblemDir = cfg.Resolve(cfg.AuthorProblemsDir);
        ServerRoot = cfg.Resolve(cfg.ServerRoot);
        ServerProblemDir = cfg.Resolve(cfg.ServerProblemsDir);
        TestDataDir = cfg.Resolve(cfg.TestDataDir);
        TempDir = cfg.Resolve(cfg.TempDir);
        SessionFile = cfg.Resolve(cfg.SessionFile);
    }
}
