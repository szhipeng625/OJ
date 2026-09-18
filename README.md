# OJ 在线判题系统

一个把 **WPF 客户端 + 分布式服务端 + LSM 存储 + 在线判题** 串起来的 C/S 产品。
整合自四个参考项目：

| 参考项目 | 在本系统中的角色 |
|----------|------------------|
| `D:\acmProblem\judge` | 判题核心（编译、限时运行、输出比对） |
| `D:\HMETCD-main` | Raft 分布式结构（本版先单机，接口已预留） |
| `D:\tiny-lsm-master` | LSM-Tree 存储设计（MemTable/WAL/SSTable） |
| `D:\project\lsmUI` | WPF + HandyControl 前端模板 |

## 架构

```
┌─────────────────────────────┐        HTTP/JSON         ┌──────────────────────────────┐
│  WPF 客户端 (C#/.NET)        │ ◀──────────────────────▶ │  Go 服务端                    │
│  HandyControl 界面          │                           │                              │
│  · 题目列表/描述              │                           │  internal/api   HTTP 接口      │
│  · 代码编辑                   │                           │  internal/judge 判题(编译+限时) │
│  · 提交看结果                │                           │  internal/raft  一致性存储(单机)│
└─────────────────────────────┘                           │     └─ internal/lsm LSM 引擎  │
                                                          └──────────────────────────────┘
```

## 目录结构

```
D:\OJ\
├── server\                  Go 后端
│   ├── main.go              入口（监听 :8080）
│   ├── internal\
│   │   ├── api\             HTTP 接口（题目列表/提交/提交记录）
│   │   ├── judge\           判题：g++ 编译 + 限时运行 + 输出比对
│   │   ├── raft\            一致性存储接口（单机实现，预留分布式）
│   │   └── lsm\             迷你 LSM：MemTable + WAL + SSTable
│   └── problems\1\          示例题目 A+B（含测试点）
└── client\                  WPF 前端
    ├── OJClient.csproj      net8.0-windows + HandyControl
    ├── MainWindow.xaml      主界面
    └── Services\ApiClient.cs 后端通信封装
```

## 运行

### 1. 启动后端（需要 g++ 在 PATH）

```bat
cd D:\OJ\server
go run . -addr :8080 -problems ./problems -data ./data
```

看到 `OJ 服务端已启动: http://localhost:8080` 即就绪。
判题依赖 `g++`（本机 MinGW 13.2 已在 PATH）。

### 2. 启动前端

```bat
cd D:\OJ\client
dotnet run
```

界面里选题目 → 在代码框写 C++ → 点「提交判题」→ 底部显示 `AC/WA/TLE/CE` 和每个测试点耗时。

## 已验证的闭环

- 正确 A+B 代码 → **AC**，4 个测试点全过；
- 错误代码（永远输出 0）→ **WA**；
- 提交记录经 Raft 层落到 LSM，重启后端后仍可通过 `/api/submissions` 读到。

## 关键实现说明

**判题（`internal/judge`）**：把用户代码写临时文件 → `g++ -O2` 编译（失败判 CE）→ 对每个 `*.in` 用 `exec.CommandContext` 限时运行（超时判 TLE）→ 输出规范化（去 `\r`、行尾空格、末尾空行）后与 `*.out` 比对。

**LSM（`internal/lsm`）**：写先走 WAL 再进 MemTable（`map`）；MemTable 超阈值（256 条）就排序落盘成 SSTable（二进制：count + [keyLen, key, valLen, val]），并重置 WAL；读先查 MemTable，再从新到旧二分扫 SSTable；启动时重放 WAL。暂未做 SSTable 合并（compaction），可后续加。

**Raft（`internal/raft`）**：当前 `NewSingleNode` 直接把读写落本地 LSM。要接 HMETCD 那套分布式，只需把 `Put/Delete` 改成"经 Raft 日志复制后再 Apply"、`Get` 走 ReadIndex，上层 API 不动。

## 扩展方向

- **多节点**：把 `raft.Node` 换成 HMETCD 的 Raft 集群实现，`Put` 走复制；
- **Compaction**：在 LSM 里定期合并重叠 SSTable；
- **更多题目/语言**：在 `problems/` 加编号目录，或把 judge 的 `g++` 换成按语言选择编译器；
- **Special Judge**：非整数输出（边集、浮点数）时替换 `judge` 里的逐行比对。
