# ACMDOG

WPF 客户端 + C++ 判题核心 + **远端 LSM 存储（RESP）**，全 C/S 一体化产品，全部用 Visual Studio 构建。

```
┌──────────────────────────────┐        P/Invoke         ┌──────────────────────────────┐
│ WPF 客户端 (C#/.NET 8)        │ ◀────────────────────▶ │ ojcore.dll (C++/MSVC)        │
│ HandyControl 界面             │                        │  · 判题：g++编译+限时运行+比对 │
│ · 题目列表 / 描述             │                        │  · 提交记录 → 远端 LSM 存储    │
│ · 深色代码编辑 / 提交看结果    │                        └──────────────┬───────────────┘
└──────────────────────────────┘                                       ▼
                                                    远端 LSM 存储服务（RESP，6379）
                                                    SET / GET / INCR / HSET / HGET / HKEYS
```

## 目录结构

```
D:\OJ\
├── client\                      判题客户端（WPF，三层架构）
│   ├── client.sln               客户端解决方案（client + HandyControl + ojcore）
│   ├── Presentation\            UI 层：Views（窗口）/ Helpers；代码编辑器等控件在 shared\OjUiControls
│   ├── Business\Services\       业务逻辑层（Auth/Judge/Submit 等服务）
│   ├── DataAccess\              数据访问层（Interop P/Invoke ojcore.dll、Models）
│   ├── App.xaml.cs              组合根（DAL → BLL → UI）
│   └── ojcore\                  C++ 判题核心（VS 2022 / v143，仅 x64）
│       ├── ojcore.sln / ojcore.vcxproj   判题 DLL（oj_init/oj_get_problems/oj_submit）
│       ├── judge.cpp            判题核心
│       └── resp_client.h/.cpp   RESP 客户端（连接远端 LSM 存储，6379）
├── author\                      出题服务端（WPF + C++，三层架构，与 client 同构）
│   ├── Author.sln
│   ├── Author\                  WPF 出题工作台
│   │   ├── Presentation\        Views（启动器/题目工作台/登录）/ Helpers（WorkspaceManager）
│   │   ├── Business\Services\   BuildService（并发编译，同题串行/跨题并行，退出 Drain）等
│   │   └── DataAccess\          Interop（authorcore/ojcore P/Invoke）、各 Client、Models
│   ├── authorcore\              C++ 出题核心 DLL（authorcore.dll，仅 x64）
│   ├── problems\                服务端题库（本地数据，git 忽略）
│   └── temp\                    标程/spj 编译产物（git 忽略）
├── shared\HandyControl\         本地 HandyControl 控件库源码（client/author 共用）
├── shared\OjEditorCore\         C++ 补全引擎（client/author 共用）
├── shared\OjCodex\              ACMDOGCodex 核心：Claude 客户端、题库工具集、用量/缓存统计
├── shared\OjUiControls\         共享 UI 层（client/author 共用）：统一代码编辑器（含括号自动
│   ├── Controls\                   闭合/配对退格/回车智能缩进）、分页题目列表、只读题面窗口
│   └── Highlighting\
│       └── CppDarkPlus.xshd     C++ 语法高亮插件（VS Code Dark+ 配色，内嵌资源，改色无需改代码）
├── server\problems\             判题/出题共用的题目目录
├── scripts\build.ps1            一键构建/打包脚本（Visual Studio 与 VS Code 共用）
├── scripts\make_portable.ps1    便携包：客户端 + 出题端 + .NET 运行时，解压即用
├── .vscode\                     VS Code 任务（tasks.json）与调试（launch.json）配置
├── docs\                        setup.sql、MYSQL.md、mysql_config.example.json
└── build_all.bat                一键打包入口（内部调用 scripts\build.ps1）
```

## 便携包（解压即用，不装 .NET）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -All -Publish      # 先发布两个应用
powershell -ExecutionPolicy Bypass -File scripts\make_portable.ps1 -DownloadRuntime
```

产物 `dist_pack\OJ.zip`（约 80 MB）+ `.sha256`，布局：

```
client\     客户端
server\     出题端
runtime\    .NET 8 桌面运行时（两个应用共用，约 162 MB）
启动客户端.bat / 启动出题端.bat / 使用说明.txt
```

启动脚本设 `DOTNET_ROOT` 指向随包的 `runtime\`，并置 `DOTNET_MULTILEVEL_LOOKUP=0`
强制只用这一份 —— 目标机**不需要预装 .NET、不需要管理员权限**。直接双击 `client.exe`
不会设置这两个变量，所以务必用启动脚本。

运行时的准备方式（脚本里已封装，手工做的话要**装两次**）：

```
dotnet-install.ps1 -Runtime dotnet         -Version 8.0.31 -InstallDir <dir>   # 带 dotnet.exe + host\fxr
dotnet-install.ps1 -Runtime windowsdesktop -Version 8.0.31 -InstallDir <dir>   # 只补桌面框架
```

⚠️ 只装 `windowsdesktop` 拿到的是 runtime pack（只有 `shared\Microsoft.WindowsDesktop.App`），
没有宿主，`DOTNET_ROOT` 起不来。另外 `Microsoft.WindowsDesktop.App` 的 zip 版同样只有 shared 层。

打包时会**剔除运行时状态**：`auth\`（含登录会话令牌）、`ojdata\`、`logs\`、`temp\`、`testdata\`、
`contests\`、`problems\`。注意 `runtimes\`（复数）不能排 —— 那是部分 NuGet 包的聚合程序集目录。

## ACMDOGCodex（出题端的 AI 助手）

出题工作台内置的 Codex 式助手，主场是**题面**：审校、补数据范围、消歧义、整理标签、重写样例，
改完直接写回题库。入口两处：启动器右上角「Codex 助手」，或题目工作台题面页的「Codex 助手」（聚焦当前题）。

**看板与仪表盘**：左侧任务看板（进行中 / 待办 / 已完成），中间「思考 · 工具 · 回复」流式展示，
右侧 token 仪表盘（输入 / 输出 / 缓存读 / 缓存写 / 计费输入 / 估算成本 / 缓存命中率 / 上一轮明细）。

**缓存命中（降 token 的主线）**：

1. **会话级缓存复用**：整段对话跨指令保留。第二条指令只在尾部追加，前面整段前缀走缓存读 ——
   命中率主要来自这里。换题自动开新会话（历史里是另一道题的上下文，既没用也拖累缓存）。
2. **缓存预热**：开面板 / 切题 / 改思考链 / 题面被写回后，后台先发一次 `max_tokens=0` 的空请求，
   把 tools+system 前缀写进缓存。总写入次数不变，只是把等待挪到用户打字那段时间，点发送时首包即命中。
   可在工具栏「预热」里关掉。
3. **前缀稳定**：渲染顺序 tools → system → messages 都打缓存断点。稳定内容（角色规约、思考链、
   题库快照）在前并带 `cache_control`；易变内容（题目正文、用户指令）一律放断点之后。
   system 拆两块各打一个断点 —— 题库变动时只失效后面那块。顶层再补自动断点，覆盖多轮工具往返的历史。
4. **思考链稳定**：自定义思考链位于缓存前缀内，只在用户编辑时变化；绝不把题号、时间戳拼进 system。

命中率看仪表盘；连续两次调用后「缓存读」若始终为 0，说明前缀里有东西在变（顺序、时间戳、题库快照）。

**推理加速**：

- **并行工具**：同一轮的多个 `tool_use` 并发执行（各自仍走按题串行闸门），少几个 RTT。工具栏「并行」可关。
- **effort 分档**：`low/medium/high/max`，默认 `high`。降档要实测后再调 —— 改题面属推理型任务，
  effort 对它的影响比聊天类大得多。
- **护栏**：`maxToolRounds` 限工具轮次，`runOutputTokenBudget` 是单次指令的输出 token 软上限。

**上下文**：

- 仪表盘直接显示**上下文**（最近一次请求的完整提示词 token 数 = 输入 + 缓存读 + 缓存写）与**会话消息条数**，
  这两个数一起看才有意义：会话攒着才能复用缓存，但上下文会线性增长。
- **自动裁剪**：超过 `maxContextMessages`（默认 40）时按「整对」裁掉最早的往返，保留首条任务消息
  （它带着题目正文）与最近往返。裁剪会让消息尾部缓存失效一次，system 前缀不受影响。
- **测上下文**：用 `count_tokens` 精确统计当前 tools + system + 历史的 token 数。
- 单条工具结果超过 `MaxToolResultChars`（默认 20000 字符）会截断，避免一次 `oj_get_problem` 把上下文撑爆。

**MCP 服务**：出题端本身就是一台 MCP 服务器，把题库能力暴露给任意 MCP 客户端：

```bash
Author.exe --mcp      # stdio 承载 MCP，不弹窗口
```

```json
{ "mcpServers": { "acmdog": { "command": "…\\Author.exe", "args": ["--mcp"] } } }
```

暴露工具：`oj_list_problems` / `oj_get_problem` / `oj_save_statement` / `oj_list_tags` / `oj_get_std` / `oj_compile_std`。
面板与 MCP 共用同一份实现（`AuthorOjToolHost` + `OjToolCatalog`），所以两条入口的行为与并发闸门完全一致。

**向量分解与检索**：题库文档拆成 TF-IDF 稀疏向量（中文按字 + 字双字，英文按词），指令与文档算余弦取 top-K。
索引按题库内容哈希缓存（题库没变不重建），查询结果按归一化查询串缓存 —— 重复问同一件事不再重算。
指令里点名题号（`P129`）时强制入选 —— 向量对短数字不敏感，但点名的题目漏掉就是帮倒忙。
题库规模不超过 `retrievalTopK` 时自动跳过检索（全量已在 system 索引里，检索纯属白算）。

**上下文布局（关键取舍）**：system 稳定块放**与指令无关**的全量紧凑索引（题号/标题/标签），
检索结果放**首条 user 消息**。原因：检索结果随指令变，塞进 system 会让每条指令的缓存前缀都失效 ——
为了检索把 prompt cache 打没，得不偿失。

**接入的模型网关**：本仓库当前配置指向 DeepSeek 的 Anthropic 兼容端点
（`baseUrl = https://api.deepseek.com/anthropic`，`model = deepseek-flash`）。
实测该端点支持 `cache_control` 缓存断点、`tool_use`、带 signature 的 thinking 块与 `effort`，
但 `max_tokens` 要求 ≥ 1（预热请求会自动从 0 退到 1），且 `cache_creation_input_tokens` 恒为 0
（由服务端自行决定写缓存，`cache_control` 更像建议）。换成官方 Anthropic 只需改 `baseUrl` 与 `model`。

**审计与用量（落库）**：Codex 对题库有写权限，所以必须留痕。中间件侧新增两张表：

- `codex_runs` —— 每次指令一行：模型 / effort / 指令原文 / 工具次数 / 耗时 / 成功与否 /
  输入·输出·缓存读·缓存写·上下文 token / 估算成本 / 时间戳，`run_uid` 唯一键（`REPLACE` 写入，客户端重试不留重复行）。
- `codex_statement_audits` —— 每次写回题面一行：题目、**改前**与**改后**的标题与正文、标签、时限内存、
  写回结果，挂在同一个 `run_uid` 上。

对应接口：`POST /api/codex/run`、`POST /api/codex/audit`、`GET /api/codex/runs`、`GET /api/codex/audits`
（均需 author/admin 鉴权）。面板工具栏「审计」可查看：左边每次指令的账，右边该次运行改过的题面（改前 → 改后）。

> ⚠️ **这两张表和四个接口需要在中间件重新构建部署后才生效**（`_mw_deploy` 是部署暂存目录）。
> 未部署时审计会静默失败，界面显示「中间件返回 404（旧版中间件可能还没有 /api/codex/* 接口）」，不影响 Codex 正常使用。

客户端直接以 HTTPS 调中间件（不经 ojcore.dll：审计是纯旁路，没必要为它给原生库加导出并重新编译）。
中间件是自签名证书，所以 `HttpClient` 显式关掉了证书校验与系统代理 —— 跟原生客户端的行为保持一致。

配置在 `config.xml` 的 `<codex>` 节：`apiKey`（也可用环境变量 `ANTHROPIC_API_KEY`）、`baseUrl`、`model`、`effort`、
`enablePromptCaching`、`enableCacheWarmup`、`useParallelTools`、`enableRetrieval`、`retrievalTopK`、
`maxContextMessages`、`thinkingChain`（自定义思考逻辑链）、`mcpEnabled` 等。

## 用 VS 打开

| 解决方案 | 内容 |
|----------|------|
| `D:\OJ\client\client.sln`   | **client**（WPF）+ **ojcore**（判题 DLL）+ HandyControl，一体化解决方案 |
| `D:\OJ\client\ojcore\ojcore.sln` | 判题核心独立解决方案：**ojcore**（判题 DLL）+ **client**（WPF 客户端） |
| `D:\OJ\author\Author.sln`   | **authorcore**（出题 DLL）+ **Author**（WPF 出题工作台）+ HandyControl |

构建顺序由工程依赖自动保证。

## 出题服务端（author）

`D:\OJ\author\Author.sln`，技术栈与客户端一致（WPF + HandyControl 本地源码 + C++ DLL）。

**工作流**（出题 → 分发 → 判题）：

```
出题工作台 Author.exe
  ① 新建题目（编号）→ ② 填题面/样例 → ③ 导入测试数据 .in（可 .out 手动配对）
  ④ 写标程 std.cpp → 保存并编译 → 一键运行生成全部 .out
  ⑤（可选）写 spj.cpp → 编译
  ⑥ 校验完整性 → 发布：复制题库 {id} → 客户端题目目录 D:\OJ\server\problems\{id}
→ 判题客户端 client.exe 启动即看到新题，提交即可判
```

**authorcore.dll 导出**：`ac_init / ac_list / ac_create / ac_save_statement / ac_compile /
ac_gen_outputs / ac_validate / ac_publish / ac_last_error / ac_free_string`。

- 服务端题库独立：`D:\OJ\author\problems\{id}\`
- 标程/spj 编译产物放 `D:\OJ\author\temp\`，不随题目分发
- 发布目标默认 `D:\OJ\server\problems`（客户端读取目录），可自定义
- 已实测：建题 → 编译标程 → 生成 3 组答案 → 校验 → 发布 → 客户端判 **AC 3/3**

## 一键出包

双击 `build_all.bat`，等价于命令行执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -All -Publish
```

流程：

1. MSBuild 构建 C++ 核心（Release **x64**）：`ojcore`、`authorcore`
2. `dotnet publish` 客户端到 `dist\`、服务端到 `dist\author\`
3. 自动补齐运行时原生依赖（ojcore.dll / libmysql.dll / mysql_config.json / authorcore.dll）

运行：`dist\client.exe`（客户端）、`dist\author\Author.exe`（服务端）。

`scripts\build.ps1` 常用参数：

| 参数 | 作用 |
|------|------|
| `-Core` | 只构建三个 C++ DLL（x64） |
| `-Client` / `-Author` | 只构建客户端 / 服务端（自动先构建 C++ 核心） |
| `-All` | 全部构建（默认） |
| `-Publish` | 发布到 `dist`（不带则只构建到各工程 bin，便于调试） |
| `-Clean` | 清理所有构建/发布产物 |
| `-Configuration Debug` | 切换配置（默认 Release） |

## 用 VS Code 构建与调试

1. 首次打开本仓库时按推荐安装 **C# Dev Kit** 与 **C/C++** 扩展（也可在 `.vscode/extensions.json` 查看）。
2. `Ctrl+Shift+B` 选择任务：
   - **构建全部（不发布，便于调试）**（默认构建任务）
   - 构建 C++ 核心 / 构建客户端 / 构建服务端
   - **打包发布到 dist（客户端 + 服务端）**
   - 清理全部构建产物
3. `F5` 选择「启动客户端 client」或「启动服务端 author」即可调试 WPF（会先自动构建对应工程）。

> 注意：三个 C++ 工程只有 `Debug|x64` 与 `Release|x64` 两个配置，脚本已固定 `/p:Platform=x64`，
> 在 Visual Studio 里也请把解决方案平台切到 **x64**，否则会报 MSB8013/MSB8020。
> 运行判题/编译还需要 MinGW（g++）与 MySQL 8.0，见下文「运行」。

## Special Judge

题目目录下放一个 `spj.cpp` 即自动启用（判题时用 g++ 编译为 spj.exe）：

- 调用约定：`spj.exe <用户输出> <标准输出> <输入>`
- 退出码 **0 = 通过**，非 0 = WA（info 记为 "special judge 未通过"）
- 每次调用限时 5s

示例 `D:\OJ\server\problems\2`（浮点数和）：spj 允许 1e-4 绝对/相对误差，
所以选手输出 `0.3` 而标准答案 `0.300000` 也判 AC。

判题引擎同时做了两处容错：输入文件带 UTF-8 BOM 时自动剥离（避免 `cin` 解析失败）；
`sample.in/sample.out` 不参与判题（仅供题目展示）。

## 运行

```bat
cd D:\OJ\client
dotnet run
```

客户端启动时自动加载 `ojcore.dll`。
题目目录复用 `D:\OJ\server\problems`；提交记录经 RESP 写入远端 LSM 存储（见 `mysql_config.json` 的 `redisHost`/`redisPort`，默认 6379）。

## 提交记录存储（远端 LSM / RESP）

提交记录与比赛报名不再落本地 tiny-lsm，改为经 **RESP（Redis 协议）** 写入远端 LSM 存储服务（默认端口 6379）：

- 在 `mysql_config.json` 中配置 `redisHost` / `redisPort`（`redisHost` 缺省与 MySQL 同机）；
- ojcore 内置轻量 RESP 客户端（`resp_client.h/.cpp`），只用 GET / SET / INCR / HSET / HGET / HKEYS；
- 存储结构（用哈希作为可枚举集合，避免全量扫描）：
  - `submission:{id}` → 提交完整 JSON（含代码）
  - `latest:{user}:{cid}:{pid}` → 最近一次提交 id
  - `progress:{user}:{cid}` → 哈希 {题目id → 判定}
  - `problem_subs:{pid}` / `contest_subs:{cid}` → 哈希 {提交id → JSON}
  - `reg:{cid}` → 哈希 {用户名 → 报名 JSON}
  - `meta:seq` → 自增提交序号

## 已验证（实测）

| 提交 | 结果 |
|------|------|
| 正确 A+B 代码 | **AC**，全测试点通过 |
| 永远输出 0 | **WA**，普通文本比对 |
| 浮点和（输出 `0.3`，未格式化） | **AC**，Special Judge 容差通过 |
| 提交记录 | 经 RESP 写入远端 LSM（`submission:{id}`），重启后可读 |

## 扩展方向

- MySQL 作为题目/用户/比赛/提交的权威存储，远端 LSM 作为提交记录与报名的共享 KV 缓存；
- Special Judge 已内置：`problems/{id}/spj.cpp` 即启用（见上文）。
