using System;
using System.IO;
using System.Xml.Linq;

namespace author.DataAccess;

/// <summary>
/// 启动配置：系统初始化时最先加载 exe 同目录的 config.xml，
/// 集中管理所有需要预处理的文本路径（DLL 加载目录 / 托管程序集目录 / DLL 文件名 /
/// 题库、数据、临时、发布等目录）。
/// 找不到 config.xml 时使用与当前代码完全一致的默认值运行，并把默认配置模板写到 exe 目录，
/// 供用户按需修改；配置损坏时回退默认值，不阻塞启动。
/// 所有相对路径均以 exe 所在目录为基准解析。
/// </summary>
public sealed class AppConfig
{
    /// <summary>当前生效的配置（App 静态构造函数中 Load 后可用）。</summary>
    public static AppConfig Current { get; private set; } = new();

    /// <summary>实际使用的配置文件路径。</summary>
    public static string ConfigFile { get; private set; } = "";

    // ---------- DLL 加载 ----------
    /// <summary>原生 DLL 加载目录（分号分隔多个候选，取第一个存在的）。</summary>
    public string DllDirs { get; set; } = "native;..\\shared\\native";

    /// <summary>第三方托管程序集目录（分号分隔多个候选，取第一个存在的）。</summary>
    public string AssemblyDirs { get; set; } = "ui;..\\shared\\ui";

    /// <summary>ACMDOGCodex 专用程序集目录（Claude SDK / MCP / Microsoft.Extensions 一族，分号分隔，取第一个存在的）。</summary>
    public string CodexDirs { get; set; } = "codex;..\\shared\\codex";

    /// <summary>判题核心 DLL 文件名（启动时从 dllDirs 预加载，DllImport 同名即命中）。</summary>
    public string OjCoreDll { get; set; } = "ojcore.dll";

    /// <summary>出题核心 DLL 文件名（仅出题端使用）。</summary>
    public string AuthorCoreDll { get; set; } = "authorcore.dll";

    // ---------- 客户端数据目录 ----------
    /// <summary>客户端题库目录。</summary>
    public string ProblemsDir { get; set; } = "problems";

    /// <summary>本地提交记录目录。</summary>
    public string DataDir { get; set; } = "ojdata";

    /// <summary>中间件（middleware）地址。配置后走 MySQL/LSM 中间层；留空则仅本地 LSM 模式。</summary>
    public string MiddlewareUrl { get; set; } = "";

    /// <summary>模板代码目录（gen.template.cpp / std.template.cpp / itf.template.cpp / entry.template.cpp）。</summary>
    public string TemplatesDir { get; set; } = "templates";

    /// <summary>客户端启动日志文件。</summary>
    public string StartupLogFile { get; set; } = "startup.log";

    // ---------- 出题端数据目录 ----------
    /// <summary>出题端自有题库目录。</summary>
    public string AuthorProblemsDir { get; set; } = "problems";

    /// <summary>客户端/服务端根目录（发布目标所在）。</summary>
    public string ServerRoot { get; set; } = "server";

    /// <summary>题目发布目标目录（本地兜底）。</summary>
    public string ServerProblemsDir { get; set; } = "server\\problems";

    /// <summary>测试数据目录。</summary>
    public string TestDataDir { get; set; } = "testdata";

    /// <summary>编译临时目录。</summary>
    public string TempDir { get; set; } = "temp";

    /// <summary>登录会话文件（相对路径含子目录）。</summary>
    public string SessionFile { get; set; } = "auth\\session.txt";

    // ---------- ACMDOGCodex（出题助手） ----------
    /// <summary>
    /// Codex 运行参数（模型 / effort / 缓存 / 思考链 / MCP）。
    /// API Key 也可以不放这里，改用环境变量 ANTHROPIC_API_KEY。
    /// </summary>
    public OjCodex.CodexSettings Codex { get; set; } = new();

    /// <summary>把当前 Codex 配置写回 config.xml 的 &lt;codex&gt; 节（面板上改完即存）。</summary>
    public void SaveCodex()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            var doc = XDocument.Load(ConfigFile);
            var root = doc.Root;
            if (root is null) return;

            var old = root.Element("codex");
            old?.Remove();

            var el = new XElement("codex",
                new XElement("apiKey", Codex.ApiKey),
                new XElement("baseUrl", Codex.BaseUrl),
                new XElement("model", Codex.Model),
                new XElement("effort", Codex.Effort),
                new XElement("maxTokens", Codex.MaxTokens),
                new XElement("maxToolRounds", Codex.MaxToolRounds),
                new XElement("runOutputTokenBudget", Codex.RunOutputTokenBudget),
                new XElement("enablePromptCaching", Codex.EnablePromptCaching),
                new XElement("cacheTtl", Codex.CacheTtl),
                new XElement("enableCacheWarmup", Codex.EnableCacheWarmup),
                new XElement("useParallelTools", Codex.UseParallelTools),
                new XElement("maxContextMessages", Codex.MaxContextMessages),
                new XElement("enableRetrieval", Codex.EnableRetrieval),
                new XElement("retrievalTopK", Codex.RetrievalTopK),
                new XElement("retrievalSnippetChars", Codex.RetrievalSnippetChars),
                new XElement("showThinking", Codex.ShowThinking),
                new XElement("mcpEnabled", Codex.McpEnabled),
                new XElement("thinkingChain", Codex.ThinkingChain));
            root.Add(el);
            doc.Save(ConfigFile);
        }
        catch { /* 写配置失败不阻塞使用，面板上会提示重试 */ }
    }

    /// <summary>解析为绝对路径：绝对路径原样返回，相对路径基于 exe 所在目录。</summary>
    public string Resolve(string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    /// <summary>在一组分号分隔的候选中返回第一个存在的目录（相对路径按 exe 目录解析），全部不存在返回 null。</summary>
    public string? FirstExistingDir(string dirs)
    {
        foreach (var d in (dirs ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Resolve(d.Trim());
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// 加载配置。显式指定 configPath 时使用之，否则使用 exe 同目录 config.xml。
    /// 必须在 App 静态构造函数中最先调用。
    /// </summary>
    public static void Load(string? configPath = null)
    {
        ConfigFile = configPath ?? Path.Combine(AppContext.BaseDirectory, "config.xml");
        if (!File.Exists(ConfigFile))
        {
            Current = new AppConfig();
            try { WriteTemplate(ConfigFile); } catch { /* 写模板失败不影响启动 */ }
            return;
        }

        try
        {
            var root = XDocument.Load(ConfigFile).Root;
            var cfg = new AppConfig();
            static string? Get(XElement? r, string tag) => r?.Element(tag)?.Value?.Trim();
            static void Set(string? v, Action<string> assign)
            {
                if (!string.IsNullOrEmpty(v)) assign(v);
            }

            Set(Get(root, "dllDirs"), v => cfg.DllDirs = v);
            Set(Get(root, "assemblyDirs"), v => cfg.AssemblyDirs = v);
            Set(Get(root, "codexDirs"), v => cfg.CodexDirs = v);
            Set(Get(root, "ojCoreDll"), v => cfg.OjCoreDll = v);
            Set(Get(root, "authorCoreDll"), v => cfg.AuthorCoreDll = v);
            Set(Get(root, "problemsDir"), v => cfg.ProblemsDir = v);
            Set(Get(root, "dataDir"), v => cfg.DataDir = v);
            Set(Get(root, "middlewareUrl"), v => cfg.MiddlewareUrl = v);
            Set(Get(root, "templatesDir"), v => cfg.TemplatesDir = v);
            Set(Get(root, "startupLogFile"), v => cfg.StartupLogFile = v);
            Set(Get(root, "authorProblemsDir"), v => cfg.AuthorProblemsDir = v);
            Set(Get(root, "serverRoot"), v => cfg.ServerRoot = v);
            Set(Get(root, "serverProblemsDir"), v => cfg.ServerProblemsDir = v);
            Set(Get(root, "testDataDir"), v => cfg.TestDataDir = v);
            Set(Get(root, "tempDir"), v => cfg.TempDir = v);
            Set(Get(root, "sessionFile"), v => cfg.SessionFile = v);

            // ---- ACMDOGCodex ----
            if (root?.Element("codex") is { } codex)
            {
                string? G(string tag) => codex.Element(tag)?.Value?.Trim();
                bool B(string tag, bool dflt) => bool.TryParse(G(tag), out var b) ? b : dflt;
                int I(string tag, int dflt) => int.TryParse(G(tag), out var n) ? n : dflt;

                Set(G("apiKey"), v => cfg.Codex.ApiKey = v);
                Set(G("baseUrl"), v => cfg.Codex.BaseUrl = v);
                Set(G("model"), v => cfg.Codex.Model = v);
                Set(G("effort"), v => cfg.Codex.Effort = v);
                Set(G("cacheTtl"), v => cfg.Codex.CacheTtl = v);
                cfg.Codex.MaxTokens = I("maxTokens", cfg.Codex.MaxTokens);
                cfg.Codex.MaxToolRounds = I("maxToolRounds", cfg.Codex.MaxToolRounds);
                cfg.Codex.RunOutputTokenBudget = I("runOutputTokenBudget", cfg.Codex.RunOutputTokenBudget);
                cfg.Codex.EnablePromptCaching = B("enablePromptCaching", cfg.Codex.EnablePromptCaching);
                cfg.Codex.ShowThinking = B("showThinking", cfg.Codex.ShowThinking);
                cfg.Codex.McpEnabled = B("mcpEnabled", cfg.Codex.McpEnabled);
                cfg.Codex.EnableCacheWarmup = B("enableCacheWarmup", cfg.Codex.EnableCacheWarmup);
                cfg.Codex.UseParallelTools = B("useParallelTools", cfg.Codex.UseParallelTools);
                cfg.Codex.MaxContextMessages = I("maxContextMessages", cfg.Codex.MaxContextMessages);
                cfg.Codex.EnableRetrieval = B("enableRetrieval", cfg.Codex.EnableRetrieval);
                cfg.Codex.RetrievalTopK = I("retrievalTopK", cfg.Codex.RetrievalTopK);
                cfg.Codex.RetrievalSnippetChars = I("retrievalSnippetChars", cfg.Codex.RetrievalSnippetChars);
                // 思考链是多行文本，不做 Trim 以保留用户缩进
                cfg.Codex.ThinkingChain = codex.Element("thinkingChain")?.Value ?? "";
            }

            Current = cfg;
        }
        catch
        {
            Current = new AppConfig();   // 配置损坏回退默认值，不阻塞启动
        }
    }

    private static void WriteTemplate(string file)
    {
        const string tpl = """
<?xml version="1.0" encoding="utf-8"?>
<!--
  OJ 启动配置：系统初始化时最先加载本文件（exe 同目录 config.xml）。
  所有相对路径均以 exe 所在目录为基准；找不到本文件时使用默认值运行，并自动生成此模板。
  dllDirs / assemblyDirs 支持分号分隔多个候选目录，取第一个存在的。
-->
<ojConfig>
  <!-- 原生 DLL 加载目录（ojcore.dll / authorcore.dll / lsm_shared.dll 等传递依赖） -->
  <dllDirs>native;..\shared\native</dllDirs>
  <!-- 第三方托管程序集目录（HandyControl / AvalonEdit / Markdig 等） -->
  <assemblyDirs>ui;..\shared\ui</assemblyDirs>
  <!-- ACMDOGCodex 专用程序集目录（Claude SDK / MCP / Microsoft.Extensions 一族） -->
  <codexDirs>codex;..\shared\codex</codexDirs>
  <!-- 原生 DLL 文件名（启动时自动从 dllDirs 预加载） -->
  <ojCoreDll>ojcore.dll</ojCoreDll>
  <authorCoreDll>authorcore.dll</authorCoreDll>

  <!-- 客户端：题库目录 / 本地存储目录（提交记录 + LSM 数据 + 判题临时目录，接口判题编译的 user.dll 输出于 存储目录\temp） -->
  <problemsDir>problems</problemsDir>
  <dataDir>ojdata</dataDir>
  <!-- 中间件地址：配置后走 MySQL/LSM 中间层（替代原 mysql_config.json），留空则仅本地 LSM 模式 -->
  <middlewareUrl></middlewareUrl>
  <!-- 模板代码目录（gen.template.cpp / std.template.cpp / itf.template.cpp / entry.template.cpp） -->
  <templatesDir>templates</templatesDir>
  <!-- 客户端启动日志 -->
  <startupLogFile>startup.log</startupLogFile>

  <!-- 出题端：自有题库 / 发布根目录 / 发布题目目录 / 测试数据 / 编译临时目录 / 会话文件 -->
  <authorProblemsDir>problems</authorProblemsDir>
  <serverRoot>server</serverRoot>
  <serverProblemsDir>server\problems</serverProblemsDir>
  <testDataDir>testdata</testDataDir>
  <tempDir>temp</tempDir>
  <sessionFile>auth\session.txt</sessionFile>

  <!-- ACMDOGCodex 出题助手：API Key 也可留空，改用环境变量 ANTHROPIC_API_KEY -->
  <codex>
    <apiKey></apiKey>
    <!-- 自定义网关（留空走官方 API） -->
    <baseUrl></baseUrl>
    <model>claude-opus-5-5</model>
    <!-- 思考力度：low / medium / high / max -->
    <effort>high</effort>
    <maxTokens>16000</maxTokens>
    <!-- 单次指令最多几轮工具调用 / 单次会话输出 token 软上限（0 = 不限） -->
    <maxToolRounds>12</maxToolRounds>
    <runOutputTokenBudget>60000</runOutputTokenBudget>
    <!-- 缓存命中是主要降本手段：开启后 tools/system/消息尾部都打缓存断点 -->
    <enablePromptCaching>true</enablePromptCaching>
    <cacheTtl>5m</cacheTtl>
    <!-- 打开面板/切题/改思考链后先空跑一次，把 tools+system 前缀预热进缓存（首包即命中） -->
    <enableCacheWarmup>true</enableCacheWarmup>
    <!-- 同一轮的多个工具调用并发执行 -->
    <useParallelTools>true</useParallelTools>
    <!-- 会话最多保留多少条消息，超出按整对裁掉最早的往返 -->
    <maxContextMessages>40</maxContextMessages>
    <!-- 向量检索：把题库拆成 TF-IDF 稀疏向量，按指令取最相关的 topK 题放进上下文 -->
    <enableRetrieval>true</enableRetrieval>
    <retrievalTopK>8</retrievalTopK>
    <!-- 检索结果每条的字符预算（转成 token 的闸门） -->
    <retrievalSnippetChars>240</retrievalSnippetChars>
    <showThinking>true</showThinking>
    <mcpEnabled>true</mcpEnabled>
    <!-- 自定义思考逻辑链：留空用内置默认链路 -->
    <thinkingChain></thinkingChain>
  </codex>
</ojConfig>
""";
        File.WriteAllText(file, tpl, new System.Text.UTF8Encoding(false));
    }
}
