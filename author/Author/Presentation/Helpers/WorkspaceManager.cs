using System.Windows;
using author.Business.Services;
using author.Presentation.Views;

namespace author.Presentation.Helpers;

/// <summary>
/// 多窗口管理：每个题目可打开一个独立工作台窗口；
/// 同一题目重复打开时激活已有窗口而不是新开，不同题目可同时打开、并行编译评测。
/// </summary>
public sealed class WorkspaceManager
{
    private readonly Workbench _workbench;
    private readonly Dictionary<int, ProblemWorkspaceWindow> _open = new();

    public WorkspaceManager(Workbench workbench) => _workbench = workbench;

    /// <summary>题目内容变化（保存/导入/发布）时触发，启动器据此刷新题目列表。</summary>
    public event Action? ProblemListChanged;

    public void NotifyProblemListChanged()
    {
        try { ProblemListChanged?.Invoke(); } catch { /* UI 刷新失败不影响工作台 */ }
    }

    /// <summary>当前已打开工作台的题目编号集合。</summary>
    public IReadOnlyCollection<int> OpenProblemIds
    {
        get { lock (_open) return _open.Keys.ToArray(); }
    }

    public void Open(int problemId)
    {
        lock (_open)
        {
            if (_open.TryGetValue(problemId, out var existing))
            {
                existing.Dispatcher.Invoke(() =>
                {
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;
                    existing.Activate();
                });
                return;
            }
        }

        // 在 UI 线程创建窗口
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            ProblemWorkspaceWindow win;
            lock (_open)
            {
                if (_open.TryGetValue(problemId, out var existing2))
                {
                    if (existing2.WindowState == WindowState.Minimized)
                        existing2.WindowState = WindowState.Normal;
                    existing2.Activate();
                    return;
                }
                win = new ProblemWorkspaceWindow(problemId, _workbench, this, _workbench.TestDataDir);
                _open[problemId] = win;
            }
            win.Closed += (_, _) => { lock (_open) _open.Remove(problemId); };
            win.Show();
            win.Activate();
        });
    }

    /// <summary>程序退出时关闭全部工作台窗口。</summary>
    public void CloseAll()
    {
        ProblemWorkspaceWindow[] windows;
        lock (_open) windows = _open.Values.ToArray();
        foreach (var w in windows)
        {
            try { w.Dispatcher.Invoke(w.Close); } catch { /* 窗口可能已关闭 */ }
        }
    }
}
