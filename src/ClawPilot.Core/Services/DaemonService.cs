using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;
using TaskStatus = ClawPilot.Core.Models.TaskStatus;

namespace ClawPilot.Core.Services;

public class DaemonService
{
    private readonly TaskQueueService _taskQueue;
    private readonly ExecutorRegistry _executorRegistry;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _isRunning;
    private DateTime? _startedAt;

    private SemaphoreSlim? _concurrencyLimiter;
    private int _activeTaskCountField;

    public int StatsProcessed { get; private set; }
    public int StatsSucceeded { get; private set; }
    public int StatsFailed { get; private set; }

    private int _consecutiveIdleCycles = 0;

    public Dictionary<string, string> CurrentTaskInfo { get; private set; } = new();
    public int ActiveTaskCount { get; private set; }

    private readonly List<Dictionary<string, object>> _executionHistory = new();
    private readonly object _historyLock = new();
    private const int MaxHistory = 500;

    public bool IsRunning => _isRunning;
    public DateTime? StartedAt => _startedAt;

    public event EventHandler<TaskCompletedEventArgs>? TaskCompleted;

    public int PollIntervalSeconds { get; set; } = 5;
    public int ErrorIntervalSeconds { get; set; } = 30;
    public int ExecutorTimeoutSeconds { get; set; } = 600;
    public int MaxConcurrency { get; set; } = 1;
    public int MaxRetries { get; set; } = 3;
    public RetryPolicy RetryPolicy { get; set; } = new();

    public void UpdateConcurrency(int newMaxConcurrency)
    {
        if (newMaxConcurrency < 1) newMaxConcurrency = 1;

        MaxConcurrency = newMaxConcurrency;

        if (_isRunning && _concurrencyLimiter != null)
        {
            _concurrencyLimiter.Dispose();
            _concurrencyLimiter = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        }

        _logger?.LogInformation("Daemon 最大并发数已更新为: {MaxConc}", MaxConcurrency);
    }

    public DaemonService(TaskQueueService taskQueue, ExecutorRegistry executorRegistry, ILogger? logger = null)
    {
        _taskQueue = taskQueue;
        _executorRegistry = executorRegistry;
        _logger = logger;
    }

    public void Start()
    {
        if (_isRunning) return;

        _cts = new CancellationTokenSource();
        _isRunning = true;
        _startedAt = DateTime.Now;
        _concurrencyLimiter = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        _pollTask = Task.Run(() => PollLoop(_cts.Token));

        _logger?.LogInformation("Daemon 已启动，轮询间隔: {Interval}s，最大并发: {MaxConc}", PollIntervalSeconds, MaxConcurrency);
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _cts?.Cancel();
        _isRunning = false;
        _logger?.LogInformation("Daemon 已停止");
    }

    private async Task PollLoop(CancellationToken ct)
    {
        _logger?.LogInformation("Daemon 轮询循环开始");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var hasWork = await DispatchAsync(ct);
                if (!hasWork)
                {
                    _consecutiveIdleCycles++;
                    var delayMs = _consecutiveIdleCycles switch
                    {
                        <= 2 => PollIntervalSeconds * 1000,
                        <= 5 => 15000,
                        _ => 30000
                    };
                    await Task.Delay(delayMs, ct);
                }
                else
                {
                    _consecutiveIdleCycles = 0;
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Daemon 轮询异常");
                try
                {
                    await Task.Delay(ErrorIntervalSeconds * 1000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _isRunning = false;
        _logger?.LogInformation("Daemon 轮询循环结束");
    }

    private async Task<bool> DispatchAsync(CancellationToken ct)
    {
        if (_concurrencyLimiter == null || !_concurrencyLimiter.Wait(0))
        {
            _logger?.LogTrace("并发限制器已满，跳过本轮调度");
            return false;
        }

        _logger?.LogTrace("尝试获取下一个待处理任务");
        var task = await _taskQueue.GetNextPendingAsync();
        if (task == null)
        {
            _concurrencyLimiter.Release();
            _logger?.LogTrace("没有待处理任务");
            return false;
        }

        _logger?.LogDebug("获取到任务 {TaskId}，类型: {TaskType}，代理: {AgentName}，消息: {Message}",
            task.Id, task.TaskType, task.AgentName, task.Message);
        _ = ExecuteTaskAsync(task, ct);
        return true;
    }

    private async Task ExecuteTaskAsync(TaskItem task, CancellationToken ct)
    {
        try
        {
            Interlocked.Increment(ref _activeTaskCountField);
            ActiveTaskCount = _activeTaskCountField;

            _logger?.LogInformation("处理任务 {TaskId}，类型: {TaskType}，代理: {AgentName}，消息: {Message}（并发: {Active}/{Max}）",
                task.Id, task.TaskType, task.AgentName, task.Message, ActiveTaskCount, MaxConcurrency);

            CurrentTaskInfo = new Dictionary<string, string>
            {
                ["task_id"] = task.Id.ToString(),
                ["agent_name"] = task.AgentName,
                ["task_type"] = task.TaskType.ToString(),
                ["started_at"] = DateTime.Now.ToString("O"),
            };

            var executor = _executorRegistry.GetExecutor(task.TaskType);
            TaskStatus status;
            string output;
            string stderr = "";
            int exitCode = 0;
            string? executorName = executor?.Name;
            var startTime = DateTime.UtcNow;

            if (executor != null)
            {
                var result = await executor.ExecuteAsync(task.AgentName, task.Message, ExecutorTimeoutSeconds, ct);
                status = result.Success ? TaskStatus.Success : TaskStatus.Failed;
                output = result.Output;
                stderr = result.Error;
                exitCode = result.ExitCode;
            }
            else
            {
                status = TaskStatus.Failed;
                output = $"不支持的任务类型: {task.TaskType}";
            }

            var durationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

            if (status == TaskStatus.Success)
            {
                await _taskQueue.ReportResultAsync(task.Id, status, output);
                _logger?.LogInformation("任务 {TaskId} 处理完毕，结果: {Status}", task.Id, status);
                RecordHistory(task, status, output, executorName, durationMs);
                StatsProcessed++;
                StatsSucceeded++;
                OnTaskCompleted(task.Id.ToString(), status, output);
            }
            else if (task.RetryCount < RetryPolicy.MaxRetries)
            {
                _logger?.LogError("任务 {TaskId} 失败，Status: {Status}, ExitCode: {ExitCode}, Stderr: {Stderr}", task.Id, status, exitCode, stderr);
                ScheduleRetry(task, output);
                OnTaskCompleted(task.Id.ToString(), status, output + $"\n[将在 {RetryPolicy.CalculateDelay(task.RetryCount)}ms 后重试]", isFinal: false);
            }
            else
            {
                _logger?.LogError("任务 {TaskId} 失败且重试次数耗尽，Status: {Status}, ExitCode: {ExitCode}, Stderr: {Stderr}", task.Id, status, exitCode, stderr);
                await _taskQueue.ReportResultAsync(task.Id, status, output);
                RecordHistory(task, status, output, executorName, durationMs);
                StatsProcessed++;
                StatsFailed++;
                OnTaskCompleted(task.Id.ToString(), status, output);
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("任务 {TaskId} 被取消", task.Id);
            var output = "任务被取消";
            await _taskQueue.ReportResultAsync(task.Id, TaskStatus.Failed, output);
            OnTaskCompleted(task.Id.ToString(), TaskStatus.Failed, output);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "任务 {TaskId} 执行异常", task.Id);
            if (task.RetryCount < RetryPolicy.MaxRetries)
            {
                ScheduleRetry(task, ex.Message);
                OnTaskCompleted(task.Id.ToString(), TaskStatus.Failed, ex.Message + $"\n[将在 {RetryPolicy.CalculateDelay(task.RetryCount)}ms 后重试]", isFinal: false);
            }
            else
            {
                await _taskQueue.ReportResultAsync(task.Id, TaskStatus.Failed, ex.Message);
                StatsFailed++;
                OnTaskCompleted(task.Id.ToString(), TaskStatus.Failed, ex.Message);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeTaskCountField);
            ActiveTaskCount = _activeTaskCountField;
            CurrentTaskInfo = new Dictionary<string, string>();
            _concurrencyLimiter?.Release();
        }
    }

    private void ScheduleRetry(TaskItem task, string output)
    {
        var delay = RetryPolicy.CalculateDelay(task.RetryCount);
        _logger?.LogInformation("任务 {TaskId} 失败，安排 {Delay}ms 后重试（{RetryCount}/{MaxRetries}，策略: {Strategy}）",
            task.Id, delay, task.RetryCount + 1, RetryPolicy.MaxRetries, RetryPolicy.Strategy);
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            await _taskQueue.ScheduleRetryAsync(task.Id, output + $"\n[Retry #{task.RetryCount + 1} after {delay}ms ({RetryPolicy.Strategy})]");
        });
    }

    private void RecordHistory(TaskItem task, TaskStatus status, string output, string? executorName = null, long? durationMs = null)
    {
        var preview = output.Length > 200 ? output[..200] + "..." : output;
        var entry = new Dictionary<string, object>
        {
            ["task_id"] = task.Id,
            ["agent_name"] = task.AgentName,
            ["task_type"] = task.TaskType.ToString(),
            ["status"] = status.ToString().ToLower(),
            ["output_preview"] = preview,
            ["executed_at"] = DateTime.Now.ToString("O"),
        };

        lock (_historyLock)
        {
            _executionHistory.Add(entry);
            if (_executionHistory.Count > MaxHistory)
            {
                _executionHistory.RemoveRange(0, _executionHistory.Count - MaxHistory);
            }
        }

        _ = PersistTaskLogAsync(task, status, output, executorName, durationMs);
    }

    private async Task PersistTaskLogAsync(TaskItem task, TaskStatus status, string output, string? executorName = null, long? durationMs = null)
    {
        try
        {
            await _taskQueue.AppendTaskLogAsync(
                task.Id, task.AgentName, task.TaskType.ToString(), status.ToString().ToLower(),
                output, task.RetryCount, executorName, durationMs);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "持久化任务日志失败，TaskId: {TaskId}", task.Id);
        }
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct = default)
    {
        var task = await _taskQueue.GetNextPendingAsync();
        if (task == null) return false;

        _logger?.LogInformation("处理任务 {TaskId}，类型: {TaskType}", task.Id, task.TaskType);

        CurrentTaskInfo = new Dictionary<string, string>
        {
            ["task_id"] = task.Id.ToString(),
            ["agent_name"] = task.AgentName,
            ["task_type"] = task.TaskType.ToString(),
            ["started_at"] = DateTime.Now.ToString("O"),
        };

        TaskStatus status;
        string output;
        string? executorName = null;
        var startTime = DateTime.UtcNow;

        try
        {
            var executor = _executorRegistry.GetExecutor(task.TaskType);
            executorName = executor?.Name;

            if (executor != null)
            {
                var result = await executor.ExecuteAsync(task.AgentName, task.Message, ExecutorTimeoutSeconds, ct);
                status = result.Success ? TaskStatus.Success : TaskStatus.Failed;
                output = result.Output;
            }
            else
            {
                status = TaskStatus.Failed;
                output = $"不支持的任务类型: {task.TaskType}";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "任务 {TaskId} 执行异常", task.Id);
            status = TaskStatus.Failed;
            output = ex.Message;
        }

        var durationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

        await _taskQueue.ReportResultAsync(task.Id, status, output);
        _logger?.LogInformation("任务 {TaskId} 处理完毕，结果: {Status}", task.Id, status);

        RecordHistory(task, status, output, executorName, durationMs);

        StatsProcessed++;
        if (status == TaskStatus.Success) StatsSucceeded++;
        else StatsFailed++;

        CurrentTaskInfo = new Dictionary<string, string>();
        OnTaskCompleted(task.Id.ToString(), status, output);
        return true;
    }

    public DaemonStatus GetStatus()
    {
        List<Dictionary<string, object>> historySnapshot;
        lock (_historyLock)
        {
            historySnapshot = _executionHistory.TakeLast(20).ToList();
        }

        return new DaemonStatus
        {
            IsRunning = _isRunning,
            StartedAtIso = _startedAt?.ToString("O"),
            UptimeSeconds = _startedAt.HasValue ? (DateTime.Now - _startedAt.Value).TotalSeconds : null,
            ActiveTaskCount = ActiveTaskCount,
            MaxConcurrency = MaxConcurrency,
            StatsProcessed = StatsProcessed,
            StatsSucceeded = StatsSucceeded,
            StatsFailed = StatsFailed,
            CurrentTaskInfo = CurrentTaskInfo,
            ExecutionHistory = historySnapshot,
            RegisteredExecutors = _executorRegistry.GetRegisteredNames(),
        };
    }

    public async Task<List<ExecutorHealthCheckResult>> CheckAllExecutorHealthAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("开始执行所有执行器健康检查");
        var results = await _executorRegistry.CheckAllHealthAsync(ct);
        var healthyCount = results.Count(r => r.IsHealthy);
        _logger?.LogInformation("健康检查完成: {Healthy}/{Total} 执行器健康", healthyCount, results.Count);
        return results;
    }

    public async Task<ExecutorHealthCheckResult?> CheckExecutorHealthAsync(TaskType taskType, CancellationToken ct = default)
    {
        return await _executorRegistry.CheckHealthAsync(taskType, ct);
    }

    private void OnTaskCompleted(string taskId, TaskStatus status, string output, bool isFinal = true)
    {
        TaskCompleted?.Invoke(this, new TaskCompletedEventArgs
        {
            TaskId = taskId,
            Status = status,
            Output = output,
            IsFinal = isFinal
        });
    }
}

public class TaskCompletedEventArgs : EventArgs
{
    public string TaskId { get; set; } = "";
    public TaskStatus Status { get; set; }
    public string Output { get; set; } = "";
    public bool IsFinal { get; set; } = true;
}
