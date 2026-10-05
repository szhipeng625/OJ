using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using author.Business.Services;
using author.DataAccess;
using OjCodex;

namespace author.Presentation.Views;

/// <summary>
/// ACMDOGCodex 主面板：左边任务看板、中间思考/工具流、右边 token 仪表盘，底部指令输入。
/// 所有题库操作都经 <see cref="AuthorOjToolHost"/>，与 MCP 服务共用同一条路径。
/// </summary>
public partial class CodexWindow : Window
{
    private readonly Workbench _wb;
    private readonly AuthorOjToolHost _host;
    private readonly int? _focusProblemId;

    private readonly ObservableCollection<TaskCard> _running = [];
    private readonly ObservableCollection<TaskCard> _pending = [];
    private readonly ObservableCollection<TaskCard> _done = [];
    private readonly ObservableCollection<StepVm> _stream = [];

    private CodexSettings _settings = AppConfig.Current.Codex;
    private readonly MiddlewareCodexRecorder _recorder;
    private CodexAgent? _agent;
    private CancellationTokenSource? _cts;

    /// <summary>本窗口累计用量（跨多次指令）。</summary>
    private CodexUsage _sessionUsage = CodexUsage.Zero;

    /// <summary>
    /// 模型候选。列在这里的是「常用档」，实际可选值是配置里的 model 加上这份清单 ——
    /// 换网关（官方 / DeepSeek / 自建）只需改 config.xml 的 baseUrl 与 model，不用改代码。
    /// </summary>
    private static readonly string[] KnownModels =
    [
        "deepseek-flash", "deepseek-v4-pro",                       // DeepSeek 的 Anthropic 兼容端点
        "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5", // Anthropic 官方
    ];

    private static readonly string[] Efforts = ["low", "medium", "high", "max"];

    private const int MaxStreamItems = 600;

    public CodexWindow(Workbench wb, AuthorOjToolHost host, int? focusProblemId)
    {
        InitializeComponent();
        _wb = wb;
        _host = host;
        _focusProblemId = focusProblemId;
        _recorder = new MiddlewareCodexRecorder();   // 令牌由原生层持有，这里不再需要 AuthService

        RunningList.ItemsSource = _running;
        PendingList.ItemsSource = _pending;
        DoneList.ItemsSource = _done;
        StreamList.ItemsSource = _stream;

        // 配置里指定的模型一定在下拉里（哪怕它不在这份常用清单中）
        var models = KnownModels.ToList();
        if (!string.IsNullOrWhiteSpace(_settings.Model) && !models.Contains(_settings.Model))
            models.Insert(0, _settings.Model);
        foreach (var m in models) ModelBox.Items.Add(m);
        foreach (var e in Efforts) EffortBox.Items.Add(e);

        LoadSettingsIntoUi();
        RefreshChainList();
        RefreshDashboard();

        _host.ProblemChanged += OnHostProblemChanged;
        Closed += (_, _) => _host.ProblemChanged -= OnHostProblemChanged;

        Loaded += async (_, _) =>
        {
            await RefreshFocusTextAsync();
            await WarmAsync();     // 开面板即预热，用户打字这段时间正好把前缀写进缓存
        };
    }

    // ---------- 初始化 ----------

    private void LoadSettingsIntoUi()
    {
        ModelBox.SelectedItem = ModelBox.Items.Contains(_settings.Model) ? _settings.Model : ModelBox.Items[0];
        EffortBox.SelectedItem = Efforts.Contains(_settings.Effort) ? _settings.Effort : "high";
        CacheBox.IsChecked = _settings.EnablePromptCaching;
        ThinkingBox.IsChecked = _settings.ShowThinking;
        WarmBox.IsChecked = _settings.EnableCacheWarmup;
        ParallelBox.IsChecked = _settings.UseParallelTools;

        if (!_settings.IsConfigured)
        {
            StatusText.Text = "未配置 API Key";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x53, 0x4F));
        }
    }

    /// <summary>
    /// 预热 tools + system 前缀：等用户真正点发送时，首包就是缓存读。
    /// 预热失败不打扰用户（服务端不支持 0 输出、网络抖动的可能性都存在）。
    /// </summary>
    private async Task WarmAsync()
    {
        if (!_settings.EnableCacheWarmup || !_settings.IsConfigured || _warming) return;
        _warming = true;
        try
        {
            WarmText.Text = "正在预热缓存前缀…";
            _agent ??= CreateAgent();
            var bank = await _host.ListProblemsAsync();
            bool ok = await _agent.WarmCacheAsync(bank);
            WarmText.Text = ok
                ? "缓存前缀已预热：下次发送的首包应直接命中缓存读。"
                : "预热未生效（接口不支持或网络异常），不影响正常使用。";
        }
        catch (Exception ex)
        {
            WarmText.Text = "预热失败：" + ex.Message;
        }
        finally
        {
            _warming = false;
        }
    }

    private async Task RefreshFocusTextAsync()
    {
        if (_focusProblemId is not { } id)
        {
            FocusText.Text = "未指定题目 · 可在指令里点名题号";
            return;
        }
        try
        {
            var doc = await _host.GetProblemAsync(id);
            FocusText.Text = doc is null
                ? $"P{id}（读取失败）"
                : $"聚焦 P{doc.Id} {doc.Title} · {doc.TimeMs}ms/{doc.MemMb}MB";
        }
        catch
        {
            FocusText.Text = $"P{id}";
        }
    }

    private void RefreshChainList()
    {
        ChainList.ItemsSource = ThinkingChain.ParseSteps(_settings.ThinkingChain);
    }

    // ---------- 工具栏 ----------

    private void ApplySettingsFromUi()
    {
        // 就地改 settings 而不重建 agent —— agent 持有的是同一个 CodexSettings 引用，
        // 这样会话历史能跨设置调整保留下来，缓存前缀不被无谓地丢掉。
        _settings.Model = ModelBox.SelectedItem as string ?? _settings.Model;
        _settings.Effort = EffortBox.SelectedItem as string ?? "high";
        _settings.EnablePromptCaching = CacheBox.IsChecked == true;
        _settings.ShowThinking = ThinkingBox.IsChecked == true;
        _settings.EnableCacheWarmup = WarmBox.IsChecked == true;
        _settings.UseParallelTools = ParallelBox.IsChecked == true;
        AppConfig.Current.SaveCodex();
    }

    private void OnEditThinkingChain(object sender, RoutedEventArgs e)
    {
        var win = new CodexThinkingChainWindow(_settings.ThinkingChain) { Owner = this };
        if (win.ShowDialog() != true || win.Result is null) return;

        _settings.ThinkingChain = win.Result;
        AppConfig.Current.SaveCodex();
        RefreshChainList();
        AddStep(CodexStepKind.Info, "思考链已更新。它位于缓存前缀内，下一次调用会重写前缀，之后照常命中。", "");
        _ = WarmAsync();   // 立刻把新前缀写回缓存，别让用户承担这次重写
    }

    // ---------- 会话与上下文 ----------

    private void OnNewSession(object sender, RoutedEventArgs e)
    {
        if (_agent is null) return;
        _agent.ResetSession();
        AddStep(CodexStepKind.Info, "已开启新会话：历史清空，下一条指令会重新写一次缓存前缀。", "");
        RefreshDashboard();
    }

    private async void OnCountContext(object sender, RoutedEventArgs e)
    {
        if (_agent is null)
        {
            AddStep(CodexStepKind.Info, "还没有会话，先发一条指令。", "");
            return;
        }
        CountBtn.IsEnabled = false;
        try
        {
            var bank = await _host.ListProblemsAsync();
            long? tokens = await _agent.CountContextTokensAsync(bank);
            AddStep(CodexStepKind.Info, tokens is null
                ? "统计失败（接口不可用或网络异常）。"
                : $"当前上下文精确统计：{tokens:N0} token（tools + system + {_agent.SessionMessageCount} 条会话消息）。", "");
        }
        finally
        {
            CountBtn.IsEnabled = true;
        }
    }

    private void OnShowAudit(object sender, RoutedEventArgs e)
    {
        var win = new CodexAuditWindow(_recorder, _focusProblemId ?? 0) { Owner = this };
        win.ShowDialog();
    }

    private void OnShowMcp(object sender, RoutedEventArgs e)
    {
        string exe = Environment.ProcessPath ?? "Author.exe";
        MessageBox.Show(this,
            "出题端内置 MCP 服务（stdio）。把下面这条加进 MCP 客户端配置即可连上本机出题端：\n\n"
            + "{\n"
            + "  \"mcpServers\": {\n"
            + "    \"acmdog\": {\n"
            + "      \"command\": \"" + exe + "\",\n"
            + "      \"args\": [\"--mcp\"]\n"
            + "    }\n"
            + "  }\n"
            + "}\n\n"
            + "暴露的工具：\n  " + string.Join("\n  ", OjToolCatalog.Names)
            + "\n\n提示：MCP 进程会独占题库的本地工作副本，建议在出题端关闭时使用。",
            "ACMDOGCodex · MCP 服务", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnQuickAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        string which = tag.StartsWith("quick:") ? tag[6..] : tag;
        string target = _focusProblemId is { } id ? $"P{id} " : "";

        InstructionBox.Text = which switch
        {
            "review" => $"{target}审校题面：逐条找出歧义、缺失的数据范围、与样例矛盾的地方，给出改动清单后写回。",
            "range" => $"{target}补数据范围：为题面补上显式的数据范围（上下界）以及由此决定的算法复杂度要求，写回题面。",
            "edge" => $"{target}补边界与歧义：把边界情况、多解限制、精度要求写进题面，写回。",
            "tags" => $"{target}整理标签：对照题库已有标签的风格，给出更贴合考点的标签并写回。",
            "sample" => $"{target}重写样例说明：样例必须与输入输出严格一致，格式写清楚，写回。",
            _ => InstructionBox.Text,
        };
        InstructionBox.Focus();
        InstructionBox.CaretIndex = InstructionBox.Text.Length;
    }

    // ---------- 发送 / 停止 ----------

    private void OnInstructionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void OnSend(object sender, RoutedEventArgs e) => _ = SendAsync();

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async Task SendAsync()
    {
        string instruction = InstructionBox.Text.Trim();
        if (instruction.Length == 0) return;
        if (IsBusy) return;

        ApplySettingsFromUi();

        if (!_settings.IsConfigured)
        {
            MessageBox.Show(this,
                "未配置 Anthropic API Key。\n\n请在 config.xml 的 <codex><apiKey> 里填写，"
                + "或设置环境变量 ANTHROPIC_API_KEY 后重启。",
                "ACMDOGCodex", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        InstructionBox.Text = "";
        var card = new TaskCard(instruction);
        _pending.Insert(0, card);

        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            card.State = "running";
            _pending.Remove(card);
            _running.Insert(0, card);
            SortCards();

            _agent ??= CreateAgent();
            ProblemDoc? focus = _focusProblemId is { } fid ? await _host.GetProblemAsync(fid) : null;
            var bank = await _host.ListProblemsAsync();

            AddStep(CodexStepKind.Info, $"开始：{instruction}", "");
            var result = await _agent.RunAsync(instruction, focus, bank, _cts.Token);

            card.Usage = result.Usage;
            card.ToolCalls = result.ToolCallCount;
            card.State = result.Ok ? "done" : "failed";
            card.Summary = result.Ok
                ? $"完成 · 工具 {result.ToolCallCount} 次 · 输出 {result.Usage.OutputTokens} tok"
                : "失败：" + result.Error;
            card.Raise();

            _running.Remove(card);
            _done.Insert(0, card);
            SortCards();

            if (result.Ok && !string.IsNullOrWhiteSpace(result.Answer))
                AddStep(CodexStepKind.Text, result.Answer, "");
            if (!result.Ok)
                AddStep(CodexStepKind.Info, "任务失败：" + result.Error, "", isError: true);
        }
        catch (Exception ex)
        {
            card.State = "failed";
            card.Summary = ex.Message;
            _running.Remove(card);
            _done.Insert(0, card);
            SortCards();
            AddStep(CodexStepKind.Info, "异常：" + ex.Message, "", isError: true);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshDashboard();
        }
    }

    private CodexAgent CreateAgent()
    {
        var agent = new CodexAgent(_settings, _host)
        {
            // 审计旁路：用量与题面改动写进中间件（失败不影响使用）
            Recorder = _recorder,
            Username = _wb.Auth.CurrentUser?.Username ?? "",
        };
        agent.StepEmitted += step => Dispatcher.Invoke(() => AddStep(step.Kind, step.Text, step.ToolName, step.IsError));
        agent.UsageUpdated += usage => Dispatcher.Invoke(() =>
        {
            _sessionUsage = _sessionUsage.Plus(usage);
            _lastRound = usage;
            RefreshDashboard();
        });
        return agent;
    }

    private CodexUsage _lastRound = CodexUsage.Zero;
    private bool _warming;

    private bool IsBusy
    {
        get => SendBtn.IsEnabled == false;
        set
        {
            SendBtn.IsEnabled = !value;
            StopBtn.IsEnabled = value;
            StatusText.Text = value ? "运行中…" : "就绪";
            StatusText.Foreground = value
                ? new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xF6))
                : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        }
    }

    private void SortCards()
    {
        // 完成的越靠前越新，这里只保证「进行中」永远在最上面
        if (_running.Count > 1)
        {
            var first = _running[0];
            _running.Clear();
            _running.Add(first);
        }
    }

    // ---------- 展示 ----------

    private void AddStep(CodexStepKind kind, string text, string toolName, bool isError = false)
    {
        if (_stream.Count > MaxStreamItems)
            _stream.RemoveAt(_stream.Count - 1);
        _stream.Insert(0, StepVm.From(kind, text, toolName, isError));
    }

    private void RefreshDashboard()
    {
        InTokens.Text = _sessionUsage.InputTokens.ToString("N0");
        OutTokens.Text = _sessionUsage.OutputTokens.ToString("N0");
        CacheReadTokens.Text = _sessionUsage.CacheReadTokens.ToString("N0");
        CacheWriteTokens.Text = _sessionUsage.CacheWriteTokens.ToString("N0");
        BillableInput.Text = _sessionUsage.TotalInputTokens.ToString("N0");

        double cost = CodexPricing.Default.Estimate(_sessionUsage);
        CostText.Text = "$" + cost.ToString("0.0000");

        double hit = _sessionUsage.CacheHitRate;
        CacheHitBar.Value = hit * 100;
        CacheHitText.Text = _sessionUsage.TotalInputTokens == 0
            ? "暂无调用"
            : $"{hit:P1}（缓存读 {_sessionUsage.CacheReadTokens:N0} / 计费输入 {_sessionUsage.TotalInputTokens:N0}）";

        LastRoundText.Text = _lastRound.TotalInputTokens == 0 && _lastRound.OutputTokens == 0
            ? "暂无调用"
            : $"输入 {_lastRound.InputTokens:N0} · 输出 {_lastRound.OutputTokens:N0}\n"
              + $"缓存读 {_lastRound.CacheReadTokens:N0} · 缓存写 {_lastRound.CacheWriteTokens:N0}\n"
              + $"命中率 {_lastRound.CacheHitRate:P0}";

        // 上下文与会话：跨指令保留会话才能复用缓存前缀，这两个数一起看才有意义
        ContextTokens.Text = _agent is null || _agent.LastContextTokens == 0
            ? "尚无"
            : $"{_agent.LastContextTokens:N0} tok";
        SessionMessages.Text = _agent is null ? "0" : _agent.SessionMessageCount.ToString("N0");
        NewSessionBtn.IsEnabled = _agent?.HasSession == true;
    }

    private void OnHostProblemChanged(int problemId)
        => Dispatcher.Invoke(() =>
        {
            AddStep(CodexStepKind.Info, $"P{problemId} 题面已写回题库，题库快照已失效。", "");
            _ = WarmAsync();   // 题库快照变了 => system 稳定块 2 变了，立刻把新前缀写回缓存
        });

    // ---------- 视图模型 ----------

    private sealed class TaskCard : System.ComponentModel.INotifyPropertyChanged
    {
        public string Title { get; }
        public string State { get; set; } = "pending";
        public string Summary { get; set; } = "";
        public CodexUsage Usage { get; set; }
        public int ToolCalls { get; set; }

        public TaskCard(string instruction)
            => Title = instruction.Length > 60 ? instruction[..60] + "…" : instruction;

        public string SubText => string.IsNullOrEmpty(Summary) ? StateText : Summary;

        private string StateText => State switch
        {
            "running" => "运行中…",
            "done" => "已完成",
            "failed" => "失败",
            _ => "排队中",
        };

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void Raise() => PropertyChanged?.Invoke(this, new(nameof(SubText)));
    }

    private sealed class StepVm
    {
        public string Header { get; init; } = "";
        public string Body { get; init; } = "";
        public Brush Brush { get; init; } = Brushes.White;
        public Brush HeaderBrush { get; init; } = Brushes.Gray;
        public FontFamily Font { get; init; } = new("Segoe UI");

        private static readonly Brush ThinkingBg = new SolidColorBrush(Color.FromRgb(0xFA, 0xF7, 0xFF));
        private static readonly Brush ToolBg = new SolidColorBrush(Color.FromRgb(0xF3, 0xF8, 0xFF));
        private static readonly Brush TextBg = new SolidColorBrush(Color.FromRgb(0xF7, 0xFB, 0xF7));
        private static readonly Brush InfoBg = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
        private static readonly Brush ErrorBg = new SolidColorBrush(Color.FromRgb(0xFD, 0xF2, 0xF2));

        public static StepVm From(CodexStepKind kind, string text, string toolName, bool isError)
        {
            if (isError)
                return new StepVm { Header = "错误", Body = text, Brush = ErrorBg, HeaderBrush = Brushes.Firebrick };

            return kind switch
            {
                CodexStepKind.Thinking => new StepVm
                {
                    Header = "思考", Body = text, Brush = ThinkingBg,
                    HeaderBrush = new SolidColorBrush(Color.FromRgb(0x7B, 0x5C, 0xC4)),
                },
                CodexStepKind.ToolCall => new StepVm
                {
                    Header = "调用工具 · " + toolName, Body = text, Brush = ToolBg,
                    HeaderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xF6)),
                    Font = new FontFamily("Consolas"),
                },
                CodexStepKind.ToolResult => new StepVm
                {
                    Header = "工具返回 · " + toolName, Body = text, Brush = ToolBg,
                    HeaderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0x66)),
                    Font = new FontFamily("Consolas"),
                },
                CodexStepKind.Text => new StepVm
                {
                    Header = "回复", Body = text, Brush = TextBg,
                    HeaderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0x66)),
                },
                _ => new StepVm { Header = "状态", Body = text, Brush = InfoBg },
            };
        }
    }
}
