using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// 任务守护服务 — 替代 Python TaskDaemon
/// 持续轮询获取任务并执行，支持并行
/// </summary>
public class DaemonService
{
    private readonly TaskQueueService _taskQueue;
    private readonly OpenClawExecutor _executor;
    private readonly ILogger? _logger;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _isRunning;
    private DateTime? _startedAt;

    // 并发控制
    private SemaphoreSlim? _concurrencyLimiter;
    private int _activeTaskCountField;

    // 执行统计
    public int StatsProcessed { get; private set; }
    public int StatsSucceeded { get; private set; }
    public int StatsFailed { get; private set; }

    // 当前任务信息
    public Dictionary<string, string> CurrentTaskInfo { get; private set; } = new();

    // 当前正在执行的任务数
    public int ActiveTaskCount { get; private set; }

    // 执行历史（内存，最近 500 条）
    private readonly List<Dictionary<string, object>> _executionHistory = new();
    private readonly object _historyLock = new();
    private const int MaxHistory = 500;

    public bool IsRunning => _isRunning;
    public DateTime? StartedAt => _startedAt;

    /// <summary>
    /// 轮询间隔（秒）
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// 出错时间隔（秒）
    /// </summary>
    public int ErrorIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 执行超时（秒）
    /// </summary>
    public int ExecutorTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// 最大并行执行数
    /// </summary>
    public int MaxConcurrency { get; set; } = 3;

    public DaemonService(TaskQueueService taskQueue, OpenClawExecutor executor, ILogger? logger = null)
    {
        _taskQueue = taskQueue;
        _executor = executor;
        _logger = logger;
    }

    // ==================== 生命周期 ====================

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

    // ==================== 核心循环 ====================

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
                    await Task.Delay(PollIntervalSeconds * 1000, ct);
                }
                // 有任务时稍微等一下再取下一批，避免瞬间取出太多
                else
                {
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

    /// <summary>
    /// 尝试获取并分派一个任务。如果并发数已满则等待，返回是否有可用任务。
    /// </summary>
    private async Task<bool> DispatchAsync(CancellationToken ct)
    {
        // 非阻塞检查：如果并发数已满，直接返回 false（等待下一轮轮询）
        if (_concurrencyLimiter == null || !_concurrencyLimiter.Wait(0))
        {
            _logger?.LogDebug("并发限制器已满，跳过本轮调度");
            return false;
        }

        _logger?.LogDebug("尝试获取下一个待处理任务");
        var task = await _taskQueue.GetNextPendingAsync();
        if (task == null)
        {
            // 没有任务，释放信号量
            _concurrencyLimiter.Release();
            _logger?.LogDebug("没有待处理任务");
            return false;
        }

        _logger?.LogDebug("获取到任务 {TaskId}，类型: {TaskType}，代理: {AgentName}，消息: {Message}",
            task.Id, task.TaskType, task.AgentName, task.Message);
        // 有任务，在后台执行（不 await，fire-and-forget 但受信号量控制）
        _ = ExecuteTaskAsync(task, ct);
        return true;
    }

    /// <summary>
    /// 并行执行单个任务
    /// </summary>
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

            // 执行
            ClawPilot.Core.Models.TaskStatus status;
            string output;
            if (task.TaskType == ClawPilot.Core.Models.TaskType.OpenClaw)
            {
                (var statusStr, output) = await _executor.ExecuteAsync(
                    task.AgentName, task.Message, ExecutorTimeoutSeconds, ct);
                
                status = statusStr == "success" ? ClawPilot.Core.Models.TaskStatus.Success : ClawPilot.Core.Models.TaskStatus.Failed;
            }
            else
            {
                status = ClawPilot.Core.Models.TaskStatus.Failed;
                output = $"不支持的任务类型: {task.TaskType}";
            }

            // 回报结果
            await _taskQueue.ReportResultAsync(task.Id, status, output);
            _logger?.LogInformation("任务 {TaskId} 处理完毕，结果: {Status}", task.Id, status);

            // 记录历史
            RecordHistory(task, status, output);

            StatsProcessed++;
            if (status == ClawPilot.Core.Models.TaskStatus.Success) StatsSucceeded++;
            else StatsFailed++;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("任务 {TaskId} 被取消", task.Id);
            await _taskQueue.ReportResultAsync(task.Id, ClawPilot.Core.Models.TaskStatus.Failed, "任务被取消");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "任务 {TaskId} 执行异常", task.Id);
            await _taskQueue.ReportResultAsync(task.Id, ClawPilot.Core.Models.TaskStatus.Failed, ex.Message);
            StatsFailed++;
        }
        finally
        {
            Interlocked.Decrement(ref _activeTaskCountField);
            ActiveTaskCount = _activeTaskCountField;
            CurrentTaskInfo = new Dictionary<string, string>();

            // 释放信号量，允许下一个任务执行
            _concurrencyLimiter?.Release();
        }
    }

    private void RecordHistory(TaskItem task, ClawPilot.Core.Models.TaskStatus status, string output)
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
    }

    /// <summary>
    /// 获取一个任务并执行（兼容旧版，同步执行单任务）
    /// </summary>
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

        // 执行
        ClawPilot.Core.Models.TaskStatus status;
        string output;
        if (task.TaskType == ClawPilot.Core.Models.TaskType.OpenClaw)
        {
            (var statusStr, output) = await _executor.ExecuteAsync(
                task.AgentName, task.Message, ExecutorTimeoutSeconds, ct);
            
            status = statusStr == "success" ? ClawPilot.Core.Models.TaskStatus.Success : ClawPilot.Core.Models.TaskStatus.Failed;
        }
        else
        {
            status = ClawPilot.Core.Models.TaskStatus.Failed;
            output = $"不支持的任务类型: {task.TaskType}";
        }

        // 回报结果
        await _taskQueue.ReportResultAsync(task.Id, status, output);
        _logger?.LogInformation("任务 {TaskId} 处理完毕，结果: {Status}", task.Id, status);

        // 记录历史
        RecordHistory(task, status, output);

        StatsProcessed++;
        if (status == ClawPilot.Core.Models.TaskStatus.Success) StatsSucceeded++;
        else StatsFailed++;

        CurrentTaskInfo = new Dictionary<string, string>();
        return true;
    }

    /// <summary>
    /// 获取 Daemon 状态
    /// </summary>
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
            StatsProcessed = StatsProcessed,
            StatsSucceeded = StatsSucceeded,
            StatsFailed = StatsFailed,
            CurrentTaskInfo = CurrentTaskInfo,
            ExecutionHistory = historySnapshot,
            RegisteredExecutors = ["openclaw"],
        };
    }
}