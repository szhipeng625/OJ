using System.Collections.Concurrent;

namespace author.Business.Services;

/// <summary>
/// 后台编译/评测任务编排中心（线程安全）。
/// <list type="bullet">
/// <item>同一题目（gateKey 相同）的任务经 <see cref="SemaphoreSlim"/> 串行执行，避免同目录 exe/数据文件竞争；</item>
/// <item>不同题目的任务在线程池上真正并行（多项目同时编译/评测）；</item>
/// <item>登记全部在途任务，程序退出时由 <see cref="DrainAsync"/> 等待所有线程结束后才放行关闭。</item>
/// </list>
/// </summary>
public sealed class BuildService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly object _pendingLock = new();
    private readonly HashSet<Task> _pending = new();

    /// <summary>在途任务数量变化时回调（可能在线程池线程触发，UI 需自行 Dispatcher）。</summary>
    public event Action<int>? PendingChanged;

    public int PendingCount
    {
        get { lock (_pendingLock) return _pending.Count; }
    }

    /// <summary>在后台线程执行一项编译/评测工作。相同 gateKey 的工作排队，不同 gateKey 并行。</summary>
    public Task<T> RunAsync<T>(string gateKey, Func<T> work)
    {
        var gate = _gates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        var task = Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return work();   // 已在线程池线程，直接执行同步原生调用
            }
            finally
            {
                gate.Release();
            }
        });
        Track(task);
        return task;
    }

    /// <summary>无返回值的重载。</summary>
    public Task RunAsync(string gateKey, Action work)
        => RunAsync(gateKey, () => { work(); return true; });

    private void Track(Task task)
    {
        lock (_pendingLock) _pending.Add(task);
        RaiseChanged();
        // 无论成功失败，结束后注销；并观察异常避免 UnobservedTaskException
        task.ContinueWith(t =>
        {
            if (t.Exception is { } ex) _ = ex.Message;
            lock (_pendingLock) _pending.Remove(task);
            RaiseChanged();
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private void RaiseChanged()
    {
        int n;
        lock (_pendingLock) n = _pending.Count;
        try { PendingChanged?.Invoke(n); } catch { /* 回调异常不影响任务管理 */ }
    }

    /// <summary>
    /// 等待所有在途后台任务结束（程序退出前调用）。
    /// 按需求不取消任务：编译 g++ 上限 60s、生成器运行上限 60s、标程生成答案单点 2s，等待是有界的。
    /// </summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] snapshot;
            lock (_pendingLock) snapshot = _pending.ToArray();
            if (snapshot.Length == 0) return;
            try { await Task.WhenAll(snapshot).ConfigureAwait(false); }
            catch { /* 单个任务失败不影响退出等待，继续检查是否有新登记任务 */ }
        }
    }
}
