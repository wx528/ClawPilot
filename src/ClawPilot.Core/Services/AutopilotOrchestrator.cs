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
    private readonly LlmDecisionEngine _llmEngine;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _isRunning;
    private DateTime? _startedAt;
    private DateTime? _lastRunAt;
    private DateTime? _nextRunAt;
    private TimeSpan _interval = TimeSpan.FromHours(1);
    private string? _lastError;
    private int _consecutiveEmptyCycles = 0;

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
        ILogger? logger = null)
    {
        _taskQueue = taskQueue;
        _storage = storage;
        _llmEngine = llmEngine;
        _logger = logger;
    }

    // ==================== 生命周期 ====================

    public Task StartAsync(TimeSpan? interval = null)
    {
        if (_isRunning) return Task.CompletedTask;

        if (interval.HasValue)
            _interval = interval.Value;

        _isRunning = true;
        _startedAt = DateTime.Now;
        _lastError = null;
        _cts = new CancellationTokenSource();

        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));

        _logger?.LogInformation("自动驾驶编排器已启动，间隔: {Interval}", _interval);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
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
                _nextRunAt = DateTime.Now + _interval;
                var delay = _interval;

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

            // 4. 调用 LLM 决策
            var elapsed = ElapsedSinceStart;
            var nextWake = DateTime.Now + _interval;

            var decision = await _llmEngine.DecideAutopilotAsync(
                goal, whiteboard, recentResults, elapsed, nextWake, ct);

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
                var result = await _taskQueue.AddTaskAsync(
                    message: task.Message,
                    agentName: task.PersonaName,
                    taskType: TaskType.OpenClaw,
                    source: TaskSource.Orchestrator);

                if (result.Success)
                {
                    scheduledCount++;
                    taskIds.Add(result.TaskId!.Value);
                    _logger?.LogInformation("任务入队成功: {Agent} - {Message}", task.PersonaName, Truncate(task.Message, 60));
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

                var fallbackResult = await _taskQueue.AddTaskAsync(
                    message: $"Mission checkpoint: Review the goal '{goal.Title}' and whiteboard. Identify at least one actionable next step or sub-goal to maintain progress.",
                    agentName: "main",
                    taskType: TaskType.OpenClaw,
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

            // 8. 记录会话完成
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
}
