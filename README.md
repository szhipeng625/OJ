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
│   ├── Presentation\            UI 层：Views（窗口）/ Controls（CodeEditor 等）/ Helpers
│   │   └── Highlighting\
│   │       └── CppDarkPlus.xshd C++ 语法高亮插件（VS Code Dark+ 配色，内嵌资源，改色无需改代码）
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
│   │   ├── Presentation\        Views（启动器/题目工作台/登录）/ Controls / Helpers（WorkspaceManager）
│   │   ├── Business\Services\   BuildService（并发编译，同题串行/跨题并行，退出 Drain）等
│   │   └── DataAccess\          Interop（authorcore/ojcore P/Invoke）、各 Client、Models
│   ├── authorcore\              C++ 出题核心 DLL（authorcore.dll，仅 x64）
│   ├── problems\                服务端题库（本地数据，git 忽略）
│   └── temp\                    标程/spj 编译产物（git 忽略）
├── shared\HandyControl\         本地 HandyControl 控件库源码（client/author 共用）
├── server\problems\             判题/出题共用的题目目录
├── scripts\build.ps1            一键构建/打包脚本（Visual Studio 与 VS Code 共用）
├── .vscode\                     VS Code 任务（tasks.json）与调试（launch.json）配置
├── docs\                        setup.sql、MYSQL.md、mysql_config.example.json
└── build_all.bat                一键打包入口（内部调用 scripts\build.ps1）
```

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
