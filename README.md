 # ACMDOG项目交流QQ群: 1109340418
 # ACMDOG首页：http://www.acmdog.cn
 # OJ 在线判题系统

WPF 客户端 + C++ 判题核心 + **tiny-lsm 分布式 LSM 存储**，全 C/S 一体化产品，全部用 Visual Studio 构建。

```
┌──────────────────────────────┐        P/Invoke         ┌──────────────────────────────┐
│ WPF 客户端 (C#/.NET 8)        │ ◀────────────────────▶ │ ojcore.dll (C++/MSVC)        │
│ HandyControl 界面             │                        │  · 判题：g++编译+限时运行+比对 │
│ · 题目列表 / 描述             │                        │  · 提交记录 → tiny-lsm 引擎   │
│ · 深色代码编辑 / 提交看结果    │                        └──────────────┬───────────────┘
└──────────────────────────────┘                                       ▼
                                                    lsm_shared.dll (tiny-lsm, VS 构建)
                                                    SkipList / MemTable / SST / WAL
                                                    WiscKey 大值分离 / MVCC 事务 / Bloom Filter
```

## 目录结构

```
D:\OJ\
├── client\                      判题客户端（WPF，三层架构）
│   ├── client.sln               客户端解决方案（client + HandyControl + ojcore + lsm_shared）
│   ├── Presentation\            UI 层：Views（窗口）/ Controls（CodeEditor 等）/ Helpers
│   │   └── Highlighting\
│   │       └── CppDarkPlus.xshd C++ 语法高亮插件（VS Code Dark+ 配色，内嵌资源，改色无需改代码）
│   ├── Business\Services\       业务逻辑层（Auth/Judge/Submit 等服务）
│   ├── DataAccess\              数据访问层（Interop P/Invoke ojcore.dll、Models）
│   ├── App.xaml.cs              组合根（DAL → BLL → UI）
│   └── ojcore\                  C++ 判题核心（VS 2022 / v143，仅 x64）
│       ├── ojcore.sln / ojcore.vcxproj   判题 DLL（oj_init/oj_get_problems/oj_submit）
│       ├── judge.cpp            判题核心
│       └── lsm\                 tiny-lsm 源码 → lsm_shared.dll（C++20）
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
| `D:\OJ\client\client.sln`   | **client**（WPF）+ **ojcore**（判题 DLL）+ **lsm_shared**（tiny-lsm）+ HandyControl，一体化解决方案 |
| `D:\OJ\client\ojcore\ojcore.sln` | 判题核心独立解决方案：**lsm_shared**（tiny-lsm 动态库）+ **ojcore**（判题 DLL）+ **client**（WPF 客户端） |
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

1. MSBuild 构建 C++ 核心（Release **x64**）：`ojcore`、`lsm_shared`、`authorcore`
2. `dotnet publish` 客户端到 `dist\`、服务端到 `dist\author\`
3. 自动补齐运行时原生依赖（ojcore.dll / lsm_shared.dll / libmysql.dll / mysql_config.json / authorcore.dll）

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

客户端启动时自动加载 `ojcore.dll` → `lsm_shared.dll`（已复制到输出目录）。
题目目录复用 `D:\OJ\server\problems`；提交记录写入客户端目录 `ojdata\`（tiny-lsm：WAL 先落盘，MemTable 超阈值刷 SST）。

## tiny-lsm 用 VS 构建（本次改造）

原来 tiny-lsm 用 **xmake** 构建；现在新增 `lsm\lsm_shared.vcxproj`，用 MSBuild/VS 直接编译：

- C++20（`/std:c++20`）+ `/utf-8`
- 定义 `TINYLSM_EXPORTS` → `TINYLSM_API` 展开为 `dllexport`，导出 `tiny_lsm::LSM` 等
- 依赖 spdlog / toml11（header-only），已放 `lsm\third_party\`
- 输出 `lsm\bin\Release\lsm_shared.dll` + `.lib`
- ojcore 通过 `TINYLSM_USE_DLL` + ProjectReference 动态链接它

## 已验证（实测）

| 提交 | 结果 |
|------|------|
| 正确 A+B 代码 | **AC**，全测试点通过 |
| 永远输出 0 | **WA**，普通文本比对 |
| 浮点和（输出 `0.3`，未格式化） | **AC**，Special Judge 容差通过 |
| 提交记录 | 经 tiny-lsm 落盘（`submission:{id}`），重启后可读 |
| 崩溃恢复 | 进程被强杀后遗留的 0 字节 `tranc_id` / 截断 WAL，重启自动恢复，不再崩溃 |

## 已知问题与防护（2026-09-19 修复）

**现象**：client 启动时调用 `oj_init` 抛 `SEHException`，进程崩溃。
**根因**：进程非正常退出（强杀/崩溃）后，tiny-lsm 数据目录遗留：
1. **0 字节 `tranc_id` 文件** → `read_tranc_id_file()` 调 `read_uint64` 抛 `out_of_range`；
2. **截断的 WAL 记录** → `Record::decode` 抛 `runtime_error`。

这些 C++ 异常逃逸出 `extern "C"` 导出函数（`oj_init`），穿过 P/Invoke 边界后表现为 SEHException。

**已修复**：
- `lsm/src/lsm/transation.cpp`：`read_tranc_id_file` 对空/损坏文件容错（回退默认值）；`check_recover` 对空集合兜底；
- `lsm/src/wal/record.cpp`：`Record::decode` 对尾部截断记录改为忽略（崩溃恢复语义）；
- `ojcore/api.cpp`：`oj_init` 增加 SEH + C++ 双重异常防护，任何 native 异常转错误码返回，**绝不穿过 P/Invoke 崩溃宿主**；存储写入失败也不影响判题结果。

数据目录无需手工清理——旧数据会自动恢复（WAL 中未完成事务重放）。

## 扩展方向

- tiny-lsm 自带 **MVCC 事务 / WiscKey 大值分离 / Bloom Filter / Block Cache**，已在 DLL 中可用；
- 需要分布式时，把判题调度做成多节点，提交记录走 tiny-lsm 的 WAL 同步复制；
- Special Judge 已内置：`problems/{id}/spj.cpp` 即启用（见上文）。
