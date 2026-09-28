# 客户端 / 服务端原生委托（P/Invoke）机制说明

> 适用工程：`client`（判题客户端，WPF / .NET 8）、`author/Author`（出题服务端，WPF / .NET 8）、
> 原生核心 `client/ojcore`（ojcore.dll）、`author/authorcore`（authorcore.dll）。
>
> 本文说明两端 C# 代码如何把工作**委托**给 C++ 原生 DLL：DLL 如何被找到、函数如何声明、
> 字符串与内存在跨语言边界时如何管理、并发与异常如何处理，以及新增一个原生接口的标准步骤。

---

## 1. 为什么是"进程内委托"而不是 HTTP

早期架构中 C# 界面通过 HTTP 调用一个独立的 Go 服务。现架构把判题/出题核心改为 C++ 动态链接库，
C# 端用 .NET 的 P/Invoke（平台调用）直接在**同一进程内**调用导出函数：

| 对比项 | 旧 HTTP 服务 | 进程内 P/Invoke 委托 |
| --- | --- | --- |
| 部署 | 需额外常驻服务进程 | 随程序带 DLL，无独立进程 |
| 调用开销 | 回环网络 + 序列化 | 一次函数调用 |
| 数据交付 | HTTP body | 以 `const char*` 返回 JSON 字符串 |
| 崩溃影响 | 服务挂了界面还在 | 需在原生侧做异常隔离（见第 8 节） |

跨语言边界统一只传两类数据：**整数/指针**与 **UTF-8 字符串（内容为 JSON）**。
不跨边界传 C++ 对象、STL 容器或类实例，避免 ABI/名称改编问题。

---

## 2. 委托在三层架构中的位置

两端 WPF 工程均采用三层架构，原生委托被严格收口在数据访问层：

```
Presentation（Views / Controls / Helpers）  只绑定、展示、转发用户操作
        │  调用
Business（Services）                        业务编排、规则判断
        │  调用
DataAccess（Interop / Models / *Client）     ── Interop：原生函数声明，只做封送
                                             ── *Client：调用 Interop、JSON 反序列化为强类型模型
        │  P/Invoke
原生 DLL：ojcore.dll / authorcore.dll        判题、编译、数据库/中间件访问
```

约束：

- `DataAccess/Interop/*.cs` 只放 `[DllImport]` 声明与"取字符串并释放"的基础方法，**不含业务判断**。
- `DataAccess/*Client` 负责调用 Interop、把返回的 JSON 反序列化成 `DataAccess/Models` 中的强类型对象。
- `Presentation` 不允许直接 DllImport。

关键文件：

| 端 | Interop 声明 | DAL 客户端 |
| --- | --- | --- |
| 客户端 | `client/DataAccess/Interop/OjInterop.cs` | `client/DataAccess/ApiClient.cs` |
| 服务端 | `author/Author/DataAccess/Interop/AuthorCoreInterop.cs`、`OjCoreInterop.cs` | `AuthorClient.cs`、`GeneratorClient.cs`、`ContestClient.cs`、`AuthClient.cs` |

---

## 3. 原生 DLL 的目录布局与加载

### 3.1 发布目录

为避免 exe 根目录杂乱，发布产物按类型分目录：

```
dist/
  client.exe
  native/            ojcore.dll、libssl-3-x64.dll、libcrypto-3-x64.dll
  ui/                HandyControl.dll、ICSharpCode.AvalonEdit.dll、Markdig.dll、OjEditorCore.dll
  mysql_config.json
dist/author/
  Author.exe
  native/            ojcore.dll、authorcore.dll、OpenSSL 运行时
  ui/                同上
```

安装版还支持把两端共用的原生/界面依赖放到 `../shared/native`、`../shared/ui`，避免重复占空间。

### 3.2 启动时注册搜索路径

两端在 `App` 的静态构造函数里完成两件事（客户端 `client/App.xaml.cs`，服务端 `author/Author/App.xaml.cs`，逻辑相同）：

1. `SetDllDirectory(nativeDir)`（kernel32）——把原生目录设为 DLL 搜索目录。
   这样 P/Invoke 按 `"ojcore.dll"` 裸名加载时能找到它，**且 ojcore.dll 的传递依赖
   （libssl / libcrypto）也从同一目录解析**。目录不存在时回退到 `..\shared\native`。
2. `AssemblyLoadContext.Default.Resolving` 挂接——托管的第三方界面程序集从 `ui/`
   （回退 `..\shared\ui`）加载。

```csharp
string nativeDir = Path.Combine(baseDir, "native");
if (!Directory.Exists(nativeDir))
    nativeDir = Path.GetFullPath(Path.Combine(baseDir, "..", "shared", "native"));
if (Directory.Exists(nativeDir)) SetDllDirectory(nativeDir);

AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    string p = Path.Combine(uiDir, name.Name + ".dll");
    return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
};
```

---

## 4. 委托函数的标准写法

### 4.1 声明：DllImport + Cdecl

所有原生函数都用 `extern "C"` + `__declspec(dllexport)` 导出（见 `ojcore.h` 的 `OJ_API` 宏），
C# 侧统一 `CallingConvention.Cdecl`：

```csharp
private const string Dll = "ojcore.dll";

[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
private static extern int oj_init_mysql([MarshalAs(UnmanagedType.LPUTF8Str)] string host, int port,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string user,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string pass,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string db,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string problem_dir,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string data_dir);
```

### 4.2 字符串方向与编码

- **C# → C++（入参）**：用 `[MarshalAs(UnmanagedType.LPUTF8Str)] string`，封送器自动转成
  UTF-8 并以 `\0` 结尾。C++ 侧直接按 `const char*` 接收。
- **C++ → C#（返回值）**：原生函数返回 `const char*`（`IntPtr`），**不使用封送器自动回收**，
  而是由 C# 读完后显式调用释放函数（见 4.3）。

### 4.3 内存所有权契约（最关键）

跨边界返回字符串时，所有权按"谁分配谁释放、但释放函数成对导出"的规则：

- C++ 侧用 `dup()` 把结果复制进 `malloc` 内存并返回：

```cpp
// api.cpp
const char* dup(const std::string& s) {
    char* p = (char*)malloc(s.size() + 1);
    memcpy(p, s.c_str(), s.size() + 1);
    return p;
}

OJ_API void oj_free_string(const char* s) {
    if (s) free((void*)s);
}
```

- C# 侧用统一的 `PtrToString`（客户端）/ `Take`（服务端）模式：**先按 UTF-8 读出，再在
  `finally` 中调用对应的释放函数**，杜绝原生堆泄漏：

```csharp
// 客户端 OjInterop.cs
private static string PtrToString(IntPtr p)
{
    if (p == IntPtr.Zero) return "[]";
    try { return Marshal.PtrToStringUTF8(p) ?? "[]"; }
    finally { oj_free_string(p); }
}
```

```csharp
// 服务端 OjCoreInterop.cs —— 手写按字节读取（与上面等价，兼容旧运行时写法）
internal static string Take(IntPtr p)
{
    if (p == IntPtr.Zero) return "";
    int len = 0; while (Marshal.ReadByte(p, len) != 0) len++;
    var bytes = new byte[len];
    Marshal.Copy(p, bytes, 0, len);
    oj_free_string(p);
    return System.Text.Encoding.UTF8.GetString(bytes);
}
```

`authorcore.dll` 对应同样的一对：返回值由 `ac_free_string` 释放，封装在
`AuthorCoreInterop.Take` 中。

> 规则总结：**任何返回 `IntPtr`/`const char*` 的原生导出，调用方都必须经过 `Take/ PtrToString`，
> 绝不能直接丢弃指针。**

### 4.4 对外包装成托管方法

Interop 类再提供与原生函数一一对应的 public 方法，布尔/枚举语义在边界用 int 表达：

```csharp
public static string SubmitExJson(int problemId, string code, string username, bool virt)
    => PtrToString(oj_submit_ex(problemId, code, username, virt ? 1 : 0));
```

DAL 客户端在后台线程调用并反序列化：

```csharp
public Task<SubmitResult?> SubmitExAsync(int problemId, string code, string username, bool virt)
    => Task.Run(() => JsonSerializer.Deserialize<SubmitResult>(
        OJInterop.SubmitExJson(problemId, code, username, virt), Opt));
```

---

## 5. 客户端委托的原生能力（ojcore.dll）

`OjInterop.cs` 按功能分组导出（均为 Cdecl）：

| 分组 | 代表函数 |
| --- | --- |
| 初始化 | `oj_init`、`oj_init_redis`、`oj_init_mysql`、`oj_init_middleware` |
| 题库 | `oj_get_problems`、`oj_get_problem`、`oj_mysql_sync_problem_list`、`oj_mysql_fetch_problem` |
| 判题 | `oj_submit`、`oj_submit_ex`、`oj_submit_contest` |
| 本地调试 | `oj_debug_run`、`oj_debug_test` |
| 比赛 | `oj_get_contests`、`oj_get_contest`、`oj_contest_register`、`oj_contest_registration` |
| 榜单 / 记录 | `oj_get_board`、`oj_contest_submissions`、`oj_get_submission_detail`、`oj_get_submissions` |
| 用户体系 | `oj_register`、`oj_login`、`oj_whoami`、`oj_update_profile`、`oj_logout`、`oj_list_users` |
| 内存释放 | `oj_free_string` |

初始化策略（`ApiClient` 构造函数）：先 `oj_init` 进入本地模式，再读取同目录
`mysql_config.json`；若含 `middlewareUrl` 则 `oj_init_middleware` 收口到 Linux 中间件，
`MySqlEnabled=true`。题目/比赛的全量同步延迟到登录成功后执行，避免阻塞登录窗。

---

## 6. 服务端的"双核心"委托

服务端同时加载两个原生 DLL，职责分离：

### 6.1 authorcore.dll —— 出题/编译/数据生成（耗时工作）

`AuthorCoreInterop.cs` 声明，按分组：

- 题目：`ac_init`、`ac_list`、`ac_create`、`ac_save_statement`、`ac_save_meta`、`ac_get_meta`；
- 编译发布：`ac_compile`、`ac_gen_outputs`、`ac_validate`、`ac_publish`；
- 多生成器模型：`ac_gen_create/save/compile/run/list_files/get_file/search/import_to_problem/...`（均带题目 id 与生成器 name）；
- 比赛：`ac_contest_create/list/get/publish`；
- 错误与释放：`ac_last_error`、`ac_free_string`。

### 6.2 ojcore.dll —— 云端数据库/认证（进程级单例）

服务端复用同一个 ojcore.dll，通过 `OjCoreInterop.cs` 调用登录、发布题目/比赛、生成器库、
测试点、榜单等 MySQL 相关接口。

### 6.3 两个核心的并发策略不同（重要）

- **ojcore 不是为并发设计的**：其内部 MySQL 连接等是进程级全局状态。因此 `OjCoreInterop`
  暴露一把全局锁 `Lock`，**所有 DAL 对 ojcore 的调用都在 `lock(Lock)` 内串行**；这些调用都很快。

```csharp
public static readonly object Lock = new();

public static string ListProblemsJson()
{
    lock (Lock) return Take(oj_mysql_list_problems());
}
```

- **authorcore 可并发**：其 `g_root/g_temp` 初始化后只读，`g_lastError` 为 `thread_local`，
  不同题目使用各自独立的题目目录与 temp 目录，因此多窗口、多题目可同时编译评测。

---

## 7. 线程模型：多窗口、并行、退出排空

服务端需要"开多个窗口、多个项目同时编译评测、退出时等所有线程结束"，由三个组件协作：

### 7.1 多窗口管理 —— `WorkspaceManager`

`author/Author/Presentation/Helpers/WorkspaceManager.cs`：

- 每题最多一个工作台窗口，登记在 `Dictionary<int, ProblemWorkspaceWindow>`（加锁）。
- 重复打开同一题 → 激活已有窗口（最小化则还原），不新开；不同题 → 各自独立窗口。
- 窗口关闭时从字典移除；`CloseAll()` 供程序退出时统一关闭。

### 7.2 后台任务编排 —— `BuildService`

`author/Author/Business/Services/BuildService.cs`，线程安全：

- `RunAsync(gateKey, work)` 把一项编译/评测工作丢到线程池：
  - **同一题目（gateKey 相同）**经 `SemaphoreSlim` 排队串行，避免同目录 exe/数据文件竞争；
  - **不同题目**在线程池上**真正并行**。
- 所有在途任务登记进 `_pending`，结束后注销（同时观察异常，避免 UnobservedTaskException）。
- `PendingChanged` 事件回调在途数量，UI 经 Dispatcher 刷新。

```csharp
var gate = _gates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
var task = Task.Run(async () =>
{
    await gate.WaitAsync().ConfigureAwait(false);
    try { return work(); }      // 已在线程池线程，直接执行同步原生调用
    finally { gate.Release(); }
});
Track(task);
```

### 7.3 退出时等待所有线程 —— `DrainAsync` + `OnClosing`

`MainWindow.OnClosing`（`author/Author/Presentation/Views/MainWindow.xaml.cs`）：

- 首次点关闭且仍有在途任务时，`e.Cancel = true` 拦截退出，界面提示"正在等待 N 个任务…"；
- `await BuildService.DrainAsync()`：循环对在途任务快照 `Task.WhenAll`，**不取消任务**
  （编译/生成器运行均有 60s 上限，等待有界），直到在途数为 0；
- 再跑一个 Dispatcher 周期收尾，随后 `WorkspaceManager.CloseAll()` 关闭全部工作台，最后
  `Application.Current.Shutdown()` 真正退出。

```csharp
public async Task DrainAsync()
{
    while (true)
    {
        Task[] snapshot;
        lock (_pendingLock) snapshot = _pending.ToArray();
        if (snapshot.Length == 0) return;
        try { await Task.WhenAll(snapshot).ConfigureAwait(false); }
        catch { /* 单个失败不影响退出等待，继续检查新登记任务 */ }
    }
}
```

客户端则统一在 DAL 用 `Task.Run(...)` 把原生调用移出 UI 线程（见 `ApiClient` 全部 `*Async`
方法），保证判题/编译期间界面不卡顿。

---

## 8. 原生异常隔离

P/Invoke 的代价是原生代码崩溃会拖垮整个进程，因此原生侧做了两层防护：

1. `SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX)`
   ——初始化时关闭系统错误弹窗（缺 DLL、GP 错误等不弹模态框）。
2. 可能访问违例的路径用 SEH 包住，把异常转成错误码返回，**不让原生异常穿越 P/Invoke 边界**：

```cpp
static int redis_connect_cpp(const std::string& host, int port, std::string& errOut) {
    try { return g_redis.connect(host, port) ? 0 : -1; }
    catch (const std::exception& e) { errOut = e.what(); return -1; }
    catch (...) { errOut = "unknown C++ exception"; return -1; }
}
static int init_redis_safe(...) {
    __try { return redis_connect_cpp(...); }
    __except (EXCEPTION_EXECUTE_HANDLER) { errOut = "native access violation"; return -2; }
}
```

此外，被判用户代码运行在独立的 `entry.exe / user.exe` 子进程中（限时、限内存 Job 对象），
用户代码崩溃只影响子进程，判题核心据退出码给出 RE，不影响主程序。

---

## 9. 函数式（接口）判题中的"二级委托"

函数式判题在运行时还存在一层原生委托，与界面层的 P/Invoke 形态一致：

```
C# 提交
   └─ P/Invoke → ojcore.dll
        ├─ g++ -O2 -shared 把用户源码编译为 user.dll
        └─ 启动题目自带的 entry.exe 子进程
             entry.exe  ── LoadLibrary("user.dll")
                        ── GetProcAddress("solve")
                        ── 解析输入 → 计时调用 solve() → 写输出与耗时(ms)
```

契约：

- 用户只实现固定接口 `extern "C" __declspec(dllexport) int solve(...)`，不写 main；
  接口模板文件为题目目录下的 **`itf.template.cpp`**（`.cpp` 文件，不是 txt）。
- `entry.exe <user.dll> <输入> <输出> <时间文件>`：负责 LoadLibrary、解析输入、
  **只计时 solve 调用本身**（输入解析/输出不计入 timeMs）、写回结果与耗时。
- ojcore 对签名无感知，只做"编译 DLL → 跑入口 → 读时间文件 → 比输出"。
- 为保证客户端后台能自动编译，ojcore 的 `resolve_gpp()` 按 **PATH → 随包/常见 mingw64 位置
  （`<dll目录>\..\mingw64\bin`、`<dll目录>\mingw64\bin`、`<exe目录>\mingw64\bin`、
  `D:\mingw64\bin`）→ 裸名兜底** 的顺序解析 g++，不依赖机器 PATH 配置。
- `judge_mode=0`（未配置）的题目完全走旧的整程序判题路径，行为不变。

> 说明：DLL 只是 C++ 这类编译型语言的加载形态。未来若支持 Python 等解释型语言，
> 可由对应 runner 用解释器直接加载用户源码、无需编译 DLL；当前接口文件统一保持 C++ `.cpp`。

---

## 10. 新增一个原生委托接口的标准步骤

1. **C++ 侧**：在 `ojcore.h` / authorcore 头文件声明 `OJ_API`（extern "C"）导出；
   在对应 `.cpp` 实现。
   - 返回字符串：拼 JSON，`return dup(...)`；同时确保有对应的 `*_free_string`。
   - 可能抛异常/访问违例：try/catch（必要时 SEH）包住，转错误码。
2. **C# Interop**：在对应 `Interop/*.cs` 加 `[DllImport(..., CallingConvention=Cdecl)]`
   声明；字符串入参用 `LPUTF8Str`，字符串返回用 `IntPtr` 并经 `Take/PtrToString` 释放。
3. **C# DAL**：在 `*Client` 中加方法，`Task.Run` 后台调用并把 JSON 反序列化为 Models 模型。
4. **Business**：在对应 Service 编排；**Presentation 只调用 Service**。
5. **并发**：若新接口走 ojcore，必须在 `lock(OjCoreInterop.Lock)` 内；走 authorcore 注意
   不同题目目录隔离。
6. **验证**：`scripts/build.ps1 -All -Publish` 全量构建，并通过不同于生成路径的方式实跑验证
   （原生 P/Invoke harness 或实际界面操作）。

---

## 11. 关键文件索引

| 内容 | 路径 |
| --- | --- |
| 客户端原生声明 | `client/DataAccess/Interop/OjInterop.cs` |
| 客户端 DAL | `client/DataAccess/ApiClient.cs` |
| 客户端启动/加载 | `client/App.xaml.cs` |
| 服务端 authorcore 声明 | `author/Author/DataAccess/Interop/AuthorCoreInterop.cs` |
| 服务端 ojcore 声明 + 全局锁 | `author/Author/DataAccess/Interop/OjCoreInterop.cs` |
| 多窗口管理 | `author/Author/Presentation/Helpers/WorkspaceManager.cs` |
| 后台任务编排/退出排空 | `author/Author/Business/Services/BuildService.cs` |
| 退出拦截 | `author/Author/Presentation/Views/MainWindow.xaml.cs` |
| 导出宏定义 | `client/ojcore/ojcore.h` |
| 导出实现 / dup / free | `client/ojcore/api.cpp` |
| 编译 / g++ 解析 / 判题 | `client/ojcore/judge.cpp` |
| 题目材料化（entry/itf） | `client/ojcore/mysql_dao.cpp` |
| 全量构建脚本 | `scripts/build.ps1` |
