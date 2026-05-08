using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// 自动驾驶编排器 — 定时唤醒 LLM，根据目标+白板+执行结果安排下一小时任务
/// </summary>
public class AutopilotOrchestrator
{
    private readonly TaskQueueService _taskQueue;
    private readonly OrchestratorStorageService _storage;
    private LlmDecisionEngine _llmEngine;
    private readonly ILogger? _logger;
    private readonly LlmClientFactory? _llmClientFactory;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _isRunning;
    private DateTime? _startedAt;
    private DateTime? _lastRunAt;
    private DateTime? _nextRunAt;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
    public bool AdaptiveIntervalEnabled { get; set; } = false;
    public string AgentName { get; set; } = "main";
    public ExecutorType ExecutorType { get; set; } = ExecutorType.OpenClaw;
    public AutopilotMode Mode { get; set; } = AutopilotMode.PlanAndExecute;
    public string? PersonaPrompt { get; set; }
    private string? _lastError;
    private int _consecutiveEmptyCycles = 0;
    private DaemonService? _daemonService;

    // ReAct 防抖机制
    private bool _isReactTriggerPending = false;
    private DateTime _lastReactTriggerAt = DateTime.MinValue;
    private readonly TimeSpan _reactDebounceInterval = TimeSpan.FromSeconds(3);
    private CancellationTokenSource? _reactDebounceCts;

    public int EmptyCycleThreshold { get; set; } = 3;

    public bool IsRunning => _isRunning;
    public DateTime? LastRunAt => _lastRunAt;
    public DateTime? NextRunAt => _nextRunAt;
    public TimeSpan ElapsedSinceStart => _startedAt.HasValue ? DateTime.Now - _startedAt.Value : TimeSpan.Zero;
    public string? LastError => _lastError;

    public AutopilotOrchestrator(
        TaskQueueService taskQueue,
        OrchestratorStorageService storage,
        LlmDecisionEngine llmEngine,
        ILogger? logger = null,
        LlmClientFactory? llmClientFactory = null)
    {
        _taskQueue = taskQueue;
        _storage = storage;
        _llmEngine = llmEngine;
        _logger = logger;
        _llmClientFactory = llmClientFactory;
    }

    public void SetLlmEngine(LlmDecisionEngine engine)
    {
        _llmEngine = engine;
    }

    public void ApplyPresetLlmConfig(string? apiKey, string? baseUrl, string? model)
    {
        if (_llmClientFactory == null) return;
        if (string.IsNullOrWhiteSpace(apiKey)) return;

        var client = _llmClientFactory.GetOrCreate(
            apiKey,
            baseUrl ?? "",
            model ?? "");
        _llmEngine = new LlmDecisionEngine(client, _logger as ILogger<LlmDecisionEngine>);
        _logger?.LogInformation("编排器已切换到独立 LLM 配置: BaseUrl={BaseUrl}, Model={Model}", baseUrl ?? "(default)", model ?? "(default)");
    }

    /// <summary>
    /// 设置 DaemonService 用于订阅任务完成事件（ReAct 模式）
    /// </summary>
    public void SetDaemonService(DaemonService daemonService)
    {
        // 取消旧的订阅
        if (_daemonService != null)
        {
            _daemonService.TaskCompleted -= OnDaemonTaskCompleted;
        }

        _daemonService = daemonService;
        _daemonService.TaskCompleted += OnDaemonTaskCompleted;
        _logger?.LogInformation("已订阅 DaemonService TaskCompleted 事件");
    }

    /// <summary>
    /// Daemon 任务完成事件处理（ReAct 模式核心）
    /// 策略：任务完成即触发编排 + 3秒防抖（多个任务几乎同时完成时合并为一次触发）
    /// </summary>
    private async void OnDaemonTaskCompleted(object? sender, TaskCompletedEventArgs e)
    {
        if (!_isRunning || Mode != AutopilotMode.ReAct || _cts == null)
            return;

        // 只响应最终结果（成功 或 失败且不再重试），忽略待重试事件
        if (!e.IsFinal)
        {
            _logger?.LogDebug("[ReAct] 任务 {TaskId} 非最终结果（待重试），跳过触发", e.TaskId);
            return;
        }

        // 如果正在执行编排周期，跳过（避免重入）
        if (_isReactTriggerPending)
        {
            _logger?.LogDebug("[ReAct] 编排周期正在执行中，跳过本次触发（任务 {TaskId}）", e.TaskId);
            return;
        }

        _logger?.LogInformation("[ReAct] 任务 {TaskId} 完成，启动防抖等待（{DebounceMs}ms）", e.TaskId, _reactDebounceInterval.TotalMilliseconds);

        // 取消之前的防抖等待（如果有）
        _reactDebounceCts?.Cancel();
        _reactDebounceCts = new CancellationTokenSource();
        var currentCts = _reactDebounceCts;

        try
        {
            // 防抖等待：3秒内如果有新任务完成，会取消本次等待，重新计时
            await Task.Delay(_reactDebounceInterval, currentCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 被新的完成事件取消了，说明还在密集完成期，等下一次
            _logger?.LogDebug("[ReAct] 防抖被新完成事件打断，等待下一次触发");
            return;
        }

        // 防抖通过，检查是否仍在运行
        if (!_isRunning || Mode != AutopilotMode.ReAct || _cts == null)
            return;

        // 防重入
        if (_isReactTriggerPending)
            return;

        _isReactTriggerPending = true;
        _lastReactTriggerAt = DateTime.Now;

        try
        {
            _logger?.LogInformation("[ReAct] 防抖通过，触发编排周期");

            // 短暂延迟确保任务状态已持久化
            await Task.Delay(500);

            await ExecuteCycleAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[ReAct] 触发编排失败");
        }
        finally
        {
            _isReactTriggerPending = false;
        }
    }

    // ==================== 生命周期 ====================

    public Task StartAsync(TimeSpan? interval = null)
    {
        if (_isRunning) return Task.CompletedTask;

        if (interval.HasValue)
            Interval = interval.Value;

        _isRunning = true;
        _startedAt = DateTime.Now;
        _lastError = null;
        _cts = new CancellationTokenSource();

        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));

        _logger?.LogInformation("自动驾驶编排器已启动，间隔: {Interval}", Interval);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _reactDebounceCts?.Cancel();
        _cts?.Cancel();
        _logger?.LogInformation("自动驾驶编排器已停止");
    }

    public async Task TriggerNowAsync()
    {
        if (_isRunning && _cts != null)
        {
            _logger?.LogInformation("手动触发编排周期");
            await ExecuteCycleAsync(_cts.Token);
        }
        else
        {
            _logger?.LogWarning("编排器未运行，无法手动触发");
        }
    }

    public async Task<bool> ExecuteCycleOnceAsync(CancellationToken ct = default)
    {
        try
        {
            await ExecuteCycleAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "单次编排执行失败");
            return false;
        }
    }

    public async Task RestartAsync(TimeSpan interval)
    {
        if (_isRunning)
        {
            Stop();
            if (_loopTask != null)
            {
                try { await _loopTask; } catch { /* ignore cancellation */ }
            }
        }
        await StartAsync(interval);
    }

    // ==================== 核心循环 ====================

    private async Task RunLoopAsync(CancellationToken ct)
    {
        _logger?.LogInformation("自动驾驶循环开始");

        // 首次立即执行一次
        _nextRunAt = DateTime.Now;
        await ExecuteCycleAsync(ct);

        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                _nextRunAt = DateTime.Now + Interval;
                var delay = Interval;

                _logger?.LogInformation("下次编排时间: {NextRunAt}", _nextRunAt);
                await Task.Delay(delay, ct);

                if (!ct.IsCancellationRequested && _isRunning)
                {
                    await ExecuteCycleAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "自动驾驶循环异常");
                _lastError = ex.Message;

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _isRunning = false;
        _logger?.LogInformation("自动驾驶循环结束");
    }

    private async Task ExecuteCycleAsync(CancellationToken ct)
    {
        _logger?.LogInformation("========== 编排周期开始 ==========");
        _lastRunAt = DateTime.Now;

        var sessionId = await _storage.BeginSessionAsync();
        string? whiteboardBefore = null;

        try
        {
            // 1. 读取目标
            var goal = await _storage.GetActiveGoalAsync();
            if (goal == null)
            {
                throw new InvalidOperationException("没有活动的编排目标，请先设置目标");
            }

            // 2. 读取白板
            var whiteboard = await _storage.GetLatestWhiteboardAsync();
            whiteboardBefore = whiteboard.Content;

            // 3. 查询上一小时的执行结果
            var recentResults = await _taskQueue.ListTasksAsync(
                source: TaskSource.Orchestrator,
                timeRange: "-1 hours",
                limit: 50);

            _logger?.LogInformation("查询到上一小时 {Count} 个编排任务", recentResults.Count);

            // 3.5 闭环审核处理：检查 reviewer 任务结果，FAIL 则自动注入修改任务
            var chainTasksScheduled = await ProcessReviewChainsAsync(recentResults);
            if (chainTasksScheduled > 0)
            {
                _logger?.LogInformation("闭环审核处理：自动注入 {Count} 个修改任务", chainTasksScheduled);
            }

            // 4. 调用 LLM 决策
            var elapsed = ElapsedSinceStart;
            var nextWake = DateTime.Now + Interval;

            var decision = await _llmEngine.DecideAutopilotAsync(
                goal, whiteboard, recentResults, elapsed, nextWake,
                allowAutoExecutor: ExecutorType == ExecutorType.Auto,
                personaPrompt: PersonaPrompt, ct);

            if (decision == null)
            {
                throw new InvalidOperationException("LLM 决策返回为空或解析失败");
            }

            _logger?.LogInformation("LLM 决策: {Reasoning}", Truncate(decision.Reasoning, 100));
            _logger?.LogInformation("计划安排 {Count} 个任务", decision.TasksToAdd.Count);

            // 5. 将任务加入队列
            var scheduledCount = 0;
            var taskIds = new List<int>();
            foreach (var task in decision.TasksToAdd)
            {
                var priority = ParsePriority(task.Priority);
                var taskType = ResolveTaskType(task.TaskType, ExecutorType);
                var result = await _taskQueue.AddTaskAsync(
                    message: task.Message,
                    agentName: AgentName,
                    taskType: taskType,
                    source: TaskSource.Orchestrator,
                    dependsOnTaskId: task.DependsOnTaskId,
                    chainId: task.ChainId,
                    chainRound: task.ChainRound);

                if (result.Success)
                {
                    scheduledCount++;
                    taskIds.Add(result.TaskId!.Value);
                    _logger?.LogInformation("任务入队成功: {Agent} - {Message}", AgentName, Truncate(task.Message, 60));
                }
                else
                {
                    _logger?.LogWarning("任务入队失败: {Error}", result.Error);
                }
            }

            // 6. 空周期检测与回退
            if (scheduledCount == 0)
            {
                _consecutiveEmptyCycles++;
                _logger?.LogWarning("编排周期安排 0 个任务，连续空周期: {Count}", _consecutiveEmptyCycles);
            }
            else
            {
                _consecutiveEmptyCycles = 0;
            }

            if (_consecutiveEmptyCycles >= EmptyCycleThreshold)
            {
                _logger?.LogError("连续 {Threshold} 个周期安排 0 个任务，触发默认回退行为", EmptyCycleThreshold);
                _lastError = $"已连续 {EmptyCycleThreshold} 个周期无任务，已触发默认回退任务";

                var fallbackTaskType = ResolveTaskType(null, ExecutorType);
                var fallbackResult = await _taskQueue.AddTaskAsync(
                    message: $"Mission checkpoint: Review the goal '{goal.Title}' and whiteboard. Identify at least one actionable next step or sub-goal to maintain progress.",
                    agentName: AgentName,
                    taskType: fallbackTaskType,
                    source: TaskSource.Orchestrator);

                if (fallbackResult.Success)
                {
                    scheduledCount = 1;
                    taskIds.Add(fallbackResult.TaskId!.Value);
                    _logger?.LogInformation("默认回退任务已安排，ID: {TaskId}", fallbackResult.TaskId);
                }

                _consecutiveEmptyCycles = 0;
            }

            // 7. 更新白板
            if (!string.IsNullOrWhiteSpace(decision.WhiteboardUpdate))
            {
                var updatedWhiteboard = await _storage.UpdateWhiteboardAsync(decision.WhiteboardUpdate);
                _logger?.LogInformation("白板已更新至版本 {Version}", updatedWhiteboard.Version);
            }

            // 9. 自适应间隔
            if (AdaptiveIntervalEnabled && decision.NextIntervalMinutes.HasValue)
            {
                var suggested = decision.NextIntervalMinutes.Value;
                if (suggested >= 5 && suggested <= 1440)
                {
                    var newInterval = TimeSpan.FromMinutes(suggested);
                    if (newInterval != Interval)
                    {
                        Interval = newInterval;
                        _logger?.LogInformation("LLM 建议调整编排间隔为 {Minutes} 分钟", suggested);
                    }
                }
                else
                {
                    _logger?.LogWarning("LLM 建议的间隔 {Minutes} 超出有效范围(5-1440)，已忽略", suggested);
                }
            }

            // 10. 记录会话完成
            var rawJson = System.Text.Json.JsonSerializer.Serialize(decision, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
            });

            await _storage.CompleteSessionAsync(
                sessionId,
                decisionSummary: decision.Reasoning,
                tasksScheduled: scheduledCount,
                whiteboardBefore: whiteboardBefore,
                whiteboardAfter: decision.WhiteboardUpdate,
                rawDecisionJson: rawJson);

            _logger?.LogInformation("========== 编排周期完成，安排 {Count} 个任务 ==========", scheduledCount);
            _lastError = null;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("编排周期被取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "编排周期失败");
            _lastError = ex.Message;
            await _storage.FailSessionAsync(sessionId, ex.Message);
        }
    }

    /// <summary>
    /// 解析任务类型：Auto 模式下使用 LLM 返回的 task_type，否则使用配置的 ExecutorType
    /// </summary>
    private static TaskType ResolveTaskType(string? llmTaskType, ExecutorType configuredType)
    {
        // 非 Auto 模式，直接使用配置的执行器
        if (configuredType != ExecutorType.Auto)
        {
            return configuredType switch
            {
                ExecutorType.Hermes => TaskType.Hermes,
                ExecutorType.KimiCode => TaskType.KimiCode,
                ExecutorType.CodeBuddy => TaskType.CodeBuddy,
                ExecutorType.Aider => TaskType.Aider,
                ExecutorType.Codex => TaskType.Codex,
                ExecutorType.QwenCode => TaskType.QwenCode,
                _ => TaskType.OpenClaw
            };
        }

        // Auto 模式：解析 LLM 返回的 task_type
        return (llmTaskType?.ToLowerInvariant()) switch
        {
            "hermes" => TaskType.Hermes,
            "kimicode" or "kimi" => TaskType.KimiCode,
            "codebuddy" or "code_buddy" => TaskType.CodeBuddy,
            "aider" => TaskType.Aider,
            "codex" => TaskType.Codex,
            "qwencode" or "qwen" or "qwen-code" => TaskType.QwenCode,
            _ => TaskType.OpenClaw
        };
    }

    // ==================== 状态查询 ====================

    public async Task<AutopilotStatus> GetStatusAsync()
    {
        var goal = await _storage.GetActiveGoalAsync();
        var whiteboard = await _storage.GetLatestWhiteboardAsync();
        var totalSessions = await _storage.GetTotalSessionCountAsync();
        var totalTasks = await _storage.GetTotalTasksScheduledAsync();

        return new AutopilotStatus
        {
            IsRunning = _isRunning,
            StartedAt = _startedAt,
            LastRunAt = _lastRunAt,
            NextRunAt = _nextRunAt,
            ElapsedSinceStart = ElapsedSinceStart,
            CurrentGoal = goal?.Title ?? "(未设置)",
            CurrentWhiteboardPreview = Truncate(whiteboard.Content, 200),
            TotalSessions = totalSessions,
            TotalTasksScheduled = totalTasks,
            LastError = _lastError
        };
    }

    // ==================== 辅助方法 ====================

    private static TaskPriority ParsePriority(string? value) => value?.ToLower() switch
    {
        "low" => TaskPriority.Low,
        "high" => TaskPriority.High,
        "urgent" => TaskPriority.Urgent,
        _ => TaskPriority.Normal
    };

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private const int MaxChainRounds = 3;

    private async Task<int> ProcessReviewChainsAsync(List<TaskItem> recentResults)
    {
        var scheduledCount = 0;

        var completedReviewers = recentResults
            .Where(t => t.Status == Models.TaskStatus.Success
                     && t.Source == TaskSource.Reviewer
                     && !string.IsNullOrWhiteSpace(t.ChainId))
            .ToList();

        foreach (var reviewerTask in completedReviewers)
        {
            var reviewResult = _llmEngine.ParseReviewOutput(reviewerTask.Output);

            _logger?.LogInformation(
                "审核结果解析: ChainId={ChainId}, Round={Round}, Passed={Passed}, Issues={IssueCount}",
                reviewerTask.ChainId, reviewerTask.ChainRound, reviewResult.Passed, reviewResult.Issues.Count);

            if (reviewResult.Passed)
            {
                var whiteboardUpdate = $"✅ 审核通过 (Chain: {reviewerTask.ChainId}, Round: {reviewerTask.ChainRound})\n" +
                                      $"摘要: {reviewResult.Summary}";
                await _storage.UpdateWhiteboardAsync(whiteboardUpdate);
                _logger?.LogInformation("审核通过，白板已更新: ChainId={ChainId}", reviewerTask.ChainId);
                continue;
            }

            if (reviewerTask.ChainRound >= MaxChainRounds)
            {
                var blockUpdate = $"🚫 审核闭环已达最大轮次 ({MaxChainRounds})，暂停任务链 (Chain: {reviewerTask.ChainId})\n" +
                                  $"最后审核摘要: {reviewResult.Summary}\n" +
                                  $"未解决问题: {string.Join("; ", reviewResult.Issues)}";
                await _storage.UpdateWhiteboardAsync(blockUpdate);
                _logger?.LogWarning("审核闭环已达最大轮次 {MaxRounds}，暂停: ChainId={ChainId}", MaxChainRounds, reviewerTask.ChainId);
                continue;
            }

            var nextRound = reviewerTask.ChainRound + 1;
            var issueList = reviewResult.Issues.Count > 0
                ? string.Join("\n", reviewResult.Issues.Select((issue, i) => $"{i + 1}. {issue}"))
                : reviewResult.Summary;

            var coderMessage = $"根据审核反馈修改代码（第 {nextRound} 轮修改）\n\n" +
                              $"审核未通过原因:\n{issueList}\n\n" +
                              $"请针对以上问题进行修改。";

            var coderTaskType = ResolveTaskType(null, ExecutorType);
            var addResult = await _taskQueue.AddTaskAsync(
                message: coderMessage,
                agentName: AgentName,
                taskType: coderTaskType,
                source: TaskSource.Orchestrator,
                dependsOnTaskId: reviewerTask.Id,
                chainId: reviewerTask.ChainId,
                chainRound: nextRound);

            if (addResult.Success)
            {
                scheduledCount++;
                _logger?.LogInformation(
                    "审核未通过，已注入第 {Round} 轮修改任务: TaskId={TaskId}, ChainId={ChainId}",
                    nextRound, addResult.TaskId, reviewerTask.ChainId);

                var reviewerMessage = $"审核第 {nextRound} 轮修改结果\n\n" +
                                     $"原始问题:\n{issueList}\n\n" +
                                     $"请检查修改是否解决了以上问题。";

                var reviewerTaskType = ResolveTaskType("hermes", ExecutorType.Auto);
                var reviewerAddResult = await _taskQueue.AddTaskAsync(
                    message: reviewerMessage,
                    agentName: "reviewer",
                    taskType: reviewerTaskType,
                    source: TaskSource.Reviewer,
                    dependsOnTaskId: addResult.TaskId!.Value,
                    chainId: reviewerTask.ChainId,
                    chainRound: nextRound);

                if (reviewerAddResult.Success)
                {
                    scheduledCount++;
                    _logger?.LogInformation(
                        "已安排第 {Round} 轮审核任务: TaskId={TaskId}, ChainId={ChainId}",
                        nextRound, reviewerAddResult.TaskId, reviewerTask.ChainId);
                }
            }
        }

        return scheduledCount;
    }
}
