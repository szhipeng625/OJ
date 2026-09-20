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
├── client\                      判题客户端 + 内嵌 C++ 判题后端（client.sln 统一解决方案）
│   ├── client.sln               客户端解决方案（client + HandyControl + ojcore + lsm_shared）
│   ├── MainWindow.xaml          主界面
│   ├── Services\ApiClient.cs    P/Invoke 调 ojcore.dll
│   └── ojcore\                  C++ 判题核心（即客户端的后端判题引擎，VS 2022 / v143）
│       ├── ojcore.sln           判题核心独立解决方案（ojcore + lsm_shared + client）
│       ├── ojcore.vcxproj       判题 DLL（导出 oj_init/oj_get_problems/oj_submit）
│       ├── judge.cpp            判题核心（移植自 acmProblem\judge）
│       └── lsm\                 tiny-lsm 源码（含 lsm_shared.vcxproj，VS 动态库工程）
│           ├── lsm_shared.vcxproj   tiny-lsm → lsm_shared.dll（C++20，TINYLSM_EXPORTS）
│           ├── src\ include\       LSM 引擎源码
│           └── third_party\        spdlog / toml11（header-only，已内置）
├── author\                      出题服务端（Author.sln，WPF + C++）
│   ├── Author\                  WPF 出题工作台（HandyControl 本地源码，与 client 一致）
│   ├── authorcore\              C++ 出题核心 DLL（建题/编译标程/生成答案/校验/分发）
│   ├── problems\                服务端题库（独立于客户端）
│   └── temp\                    标程/spj 编译产物（不随题目分发）
└── server\                      （旧 Go 版后端，题目目录被客户端/服务端共用）
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

双击 `D:\OJ\build_all.bat`（或命令行执行）：

1. MSBuild 构建 `ojcore.sln`（Release x64，lsm_shared + ojcore + client）
2. `dotnet publish` 发布 WPF 客户端
3. 产物汇聚到 `D:\OJ\dist\`，并校验 4 个关键文件

运行：双击 `D:\OJ\dist\client.exe`。

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
