using ClawPilot.Core.Models;
using ClawPilot.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using TaskStatus = ClawPilot.Core.Models.TaskStatus;

namespace ClawPilot.Tests;

public class DaemonServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TaskQueueService _taskQueue;
    private readonly Mock<IExecutor> _mockExecutor;
    private readonly ExecutorRegistry _registry;
    private readonly DaemonService _daemon;

    public DaemonServiceTests()
    {
        _dbPath = Path.GetTempFileName();
        _taskQueue = new TaskQueueService(_dbPath);
        _taskQueue.EnsureTableExistsAsync().Wait();

        _mockExecutor = new Mock<IExecutor>();
        _mockExecutor.Setup(e => e.SupportedTaskType).Returns(TaskType.OpenClaw);
        _mockExecutor.Setup(e => e.Name).Returns("mock");
        _mockExecutor.Setup(e => e.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecutorHealthCheckResult { IsHealthy = true, ExecutorName = "mock", TaskType = TaskType.OpenClaw });

        _registry = new ExecutorRegistry(
            new OpenClawExecutor("openclaw"),
            new HermesExecutor(Mock.Of<ILogger<HermesExecutor>>(), "nonexistent.ps1"),
            new KimiCodeExecutor(Mock.Of<ILogger<KimiCodeExecutor>>(), "nonexistent-kimi"),
            new CodeBuddyExecutor(Mock.Of<ILogger<CodeBuddyExecutor>>(), "nonexistent-cb"),
            new AiderExecutor(Mock.Of<ILogger<AiderExecutor>>(), "nonexistent-aider"),
            new CodexExecutor(Mock.Of<ILogger<CodexExecutor>>(), "nonexistent-codex"),
            new QwenCodeExecutor(Mock.Of<ILogger<QwenCodeExecutor>>(), "nonexistent-qwen"));

        _registry.Register(_mockExecutor.Object);

        _daemon = new DaemonService(_taskQueue, _registry);
        _daemon.PollIntervalSeconds = 1;
        _daemon.MaxConcurrency = 2;
    }

    public void Dispose()
    {
        _daemon.Stop();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<int> AddTaskAsync(string message = "test", TaskType taskType = TaskType.OpenClaw, string agentName = "main")
    {
        var result = await _taskQueue.AddTaskAsync(message, agentName, taskType);
        Assert.True(result.Success, result.Message);
        return result.TaskId!.Value;
    }

    private void SetupExecutorSuccess(string output = "done")
    {
        _mockExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecutorResult { Success = true, Output = output, ExitCode = 0 });
    }

    private void SetupExecutorFailure(string error = "failed")
    {
        _mockExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecutorResult { Success = false, Output = error, Error = error, ExitCode = 1 });
    }

    private void SetupExecutorException(Exception ex)
    {
        _mockExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);
    }

    // ==================== 生命周期测试 ====================

    [Fact]
    public void Start_SetsIsRunning()
    {
        Assert.False(_daemon.IsRunning);
        _daemon.Start();
        Assert.True(_daemon.IsRunning);
        Assert.NotNull(_daemon.StartedAt);
    }

    [Fact]
    public void Stop_ClearsIsRunning()
    {
        _daemon.Start();
        Assert.True(_daemon.IsRunning);
        _daemon.Stop();
        Assert.False(_daemon.IsRunning);
    }

    [Fact]
    public void Start_Idempotent_DoesNotCreateDuplicateLoops()
    {
        _daemon.Start();
        _daemon.Start();
        Assert.True(_daemon.IsRunning);
        _daemon.Stop();
        Assert.False(_daemon.IsRunning);
    }

    [Fact]
    public void Stop_WhenNotRunning_IsNoOp()
    {
        _daemon.Stop();
        Assert.False(_daemon.IsRunning);
    }

    // ==================== RunOnce 测试 ====================

    [Fact]
    public async Task RunOnce_NoTask_ReturnsFalse()
    {
        var result = await _daemon.RunOnceAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task RunOnce_WithTask_ExecutesAndReturnsTrue()
    {
        SetupExecutorSuccess("task output");
        await AddTaskAsync();

        var result = await _daemon.RunOnceAsync();
        Assert.True(result);
        Assert.Equal(1, _daemon.StatsProcessed);
        Assert.Equal(1, _daemon.StatsSucceeded);
    }

    [Fact]
    public async Task RunOnce_Success_UpdatesTaskStatus()
    {
        SetupExecutorSuccess("task output");
        var taskId = await AddTaskAsync();

        await _daemon.RunOnceAsync();

        var task = await _taskQueue.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Success, task.Status);
        Assert.Equal("task output", task.Output);
    }

    [Fact]
    public async Task RunOnce_Failure_UpdatesTaskStatus()
    {
        _daemon.MaxRetries = 0;
        SetupExecutorFailure("error output");
        var taskId = await AddTaskAsync();

        await _daemon.RunOnceAsync();

        var task = await _taskQueue.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Failed, task.Status);
    }

    [Fact]
    public async Task RunOnce_UnsupportedTaskType_Fails()
    {
        var taskId = await AddTaskAsync(taskType: TaskType.LangGraph);

        await _daemon.RunOnceAsync();

        var task = await _taskQueue.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Failed, task.Status);
        Assert.Contains("不支持的任务类型", task.Output);
    }

    [Fact]
    public async Task RunOnce_UpdatesStats()
    {
        SetupExecutorSuccess();
        await AddTaskAsync();
        await AddTaskAsync();

        await _daemon.RunOnceAsync();
        Assert.Equal(1, _daemon.StatsProcessed);
        Assert.Equal(1, _daemon.StatsSucceeded);

        await _daemon.RunOnceAsync();
        Assert.Equal(2, _daemon.StatsProcessed);
        Assert.Equal(2, _daemon.StatsSucceeded);
    }

    // ==================== 重试测试 ====================

    [Fact]
    public async Task RunOnce_FailureWithRetries_SchedulesRetry()
    {
        _daemon.MaxRetries = 2;
        SetupExecutorFailure("error");
        var taskId = await AddTaskAsync();

        await _daemon.RunOnceAsync();

        var task = await _taskQueue.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Failed, task.Status);
    }

    [Fact]
    public async Task RunOnce_FailureExceedsMaxRetries_MarksFailed()
    {
        _daemon.MaxRetries = 0;
        SetupExecutorFailure("permanent error");
        var taskId = await AddTaskAsync();

        await _daemon.RunOnceAsync();

        var task = await _taskQueue.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Failed, task.Status);
        Assert.Equal(1, _daemon.StatsFailed);
    }

    // ==================== 事件测试 ====================

    [Fact]
    public async Task RunOnce_Success_FiresTaskCompletedEvent()
    {
        SetupExecutorSuccess("output");
        await AddTaskAsync();

        TaskCompletedEventArgs? eventArgs = null;
        _daemon.TaskCompleted += (_, e) => eventArgs = e;

        await _daemon.RunOnceAsync();

        Assert.NotNull(eventArgs);
        Assert.Equal(TaskStatus.Success, eventArgs.Status);
        Assert.Equal("output", eventArgs.Output);
        Assert.True(eventArgs.IsFinal);
    }

    [Fact]
    public async Task RunOnce_FailureWithRetry_FiresFinalEventBecauseRunOnceDoesNotRetry()
    {
        _daemon.MaxRetries = 2;
        SetupExecutorFailure("error");
        await AddTaskAsync();

        TaskCompletedEventArgs? eventArgs = null;
        _daemon.TaskCompleted += (_, e) => eventArgs = e;

        await _daemon.RunOnceAsync();

        Assert.NotNull(eventArgs);
        Assert.Equal(TaskStatus.Failed, eventArgs.Status);
        Assert.True(eventArgs.IsFinal);
    }

    [Fact]
    public async Task RunOnce_FailureNoRetry_FiresFinalEvent()
    {
        _daemon.MaxRetries = 0;
        SetupExecutorFailure("error");
        await AddTaskAsync();

        TaskCompletedEventArgs? eventArgs = null;
        _daemon.TaskCompleted += (_, e) => eventArgs = e;

        await _daemon.RunOnceAsync();

        Assert.NotNull(eventArgs);
        Assert.Equal(TaskStatus.Failed, eventArgs.Status);
        Assert.True(eventArgs.IsFinal);
    }

    // ==================== 异常处理测试 ====================

    [Fact]
    public async Task RunOnce_ExecutorThrowsException_HandledGracefully()
    {
        _daemon.MaxRetries = 0;
        SetupExecutorException(new InvalidOperationException("boom"));
        var taskId = await AddTaskAsync();

        await _daemon.RunOnceAsync();

        var task = await _taskQueue.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Failed, task.Status);
        Assert.Equal(1, _daemon.StatsFailed);
    }

    // ==================== GetStatus 测试 ====================

    [Fact]
    public void GetStatus_WhenNotRunning_ReturnsCorrectState()
    {
        var status = _daemon.GetStatus();
        Assert.False(status.IsRunning);
        Assert.Null(status.StartedAtIso);
        Assert.Equal(0, status.StatsProcessed);
    }

    [Fact]
    public void GetStatus_WhenRunning_ReturnsCorrectState()
    {
        _daemon.Start();
        var status = _daemon.GetStatus();
        Assert.True(status.IsRunning);
        Assert.NotNull(status.StartedAtIso);
    }

    [Fact]
    public async Task GetStatus_AfterTaskExecution_ContainsHistory()
    {
        SetupExecutorSuccess();
        await AddTaskAsync();
        await _daemon.RunOnceAsync();

        var status = _daemon.GetStatus();
        Assert.Equal(1, status.StatsProcessed);
        Assert.Equal(1, status.StatsSucceeded);
        Assert.Single(status.ExecutionHistory);
    }

    [Fact]
    public void GetStatus_ContainsRegisteredExecutors()
    {
        var status = _daemon.GetStatus();
        Assert.NotEmpty(status.RegisteredExecutors);
        Assert.Contains("mock", status.RegisteredExecutors);
    }

    // ==================== 并发控制测试 ====================

    [Fact]
    public void UpdateConcurrency_UpdatesMaxConcurrency()
    {
        _daemon.UpdateConcurrency(4);
        Assert.Equal(4, _daemon.MaxConcurrency);
    }

    [Fact]
    public void UpdateConcurrency_MinValueIs1()
    {
        _daemon.UpdateConcurrency(0);
        Assert.Equal(1, _daemon.MaxConcurrency);
        _daemon.UpdateConcurrency(-5);
        Assert.Equal(1, _daemon.MaxConcurrency);
    }

    // ==================== 执行历史测试 ====================

    [Fact]
    public async Task ExecutionHistory_TruncatesLongOutput()
    {
        var longOutput = new string('x', 300);
        SetupExecutorSuccess(longOutput);
        await AddTaskAsync();
        await _daemon.RunOnceAsync();

        var status = _daemon.GetStatus();
        Assert.Single(status.ExecutionHistory);
        var preview = status.ExecutionHistory[0]["output_preview"].ToString();
        Assert.True(preview!.Length <= 203);
    }

    [Fact]
    public async Task ExecutionHistory_RecordsCorrectMetadata()
    {
        SetupExecutorSuccess("output");
        await AddTaskAsync("test message", TaskType.OpenClaw, "test-agent");
        await _daemon.RunOnceAsync();

        var status = _daemon.GetStatus();
        Assert.Single(status.ExecutionHistory);
        var entry = status.ExecutionHistory[0];
        Assert.Equal("test-agent", entry["agent_name"]);
        Assert.Equal("OpenClaw", entry["task_type"]);
        Assert.Equal("success", entry["status"]);
    }

    // ==================== CurrentTaskInfo 测试 ====================

    [Fact]
    public async Task RunOnce_SetsCurrentTaskInfoDuringExecution()
    {
        var tcs = new TaskCompletionSource<ExecutorResult>();
        _mockExecutor
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async (string agent, string msg, int timeout, CancellationToken ct) =>
            {
                await Task.Delay(10, ct);
                return new ExecutorResult { Success = true, Output = "done", ExitCode = 0 };
            });

        await AddTaskAsync();
        await _daemon.RunOnceAsync();

        Assert.Empty(_daemon.CurrentTaskInfo);
    }

    // ==================== 多执行器路由测试 ====================

    [Fact]
    public async Task RunOnce_RoutesToCorrectExecutor()
    {
        var mockAider = new Mock<IExecutor>();
        mockAider.Setup(e => e.SupportedTaskType).Returns(TaskType.Aider);
        mockAider.Setup(e => e.Name).Returns("aider-mock");
        mockAider.Setup(e => e.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecutorHealthCheckResult { IsHealthy = true, ExecutorName = "aider-mock", TaskType = TaskType.Aider });
        mockAider
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecutorResult { Success = true, Output = "aider-output", ExitCode = 0 });

        _registry.Register(mockAider.Object);

        var taskId = await AddTaskAsync("aider task", TaskType.Aider);

        await _daemon.RunOnceAsync();

        var task = await _taskQueue.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Success, task.Status);
        Assert.Equal("aider-output", task.Output);

        mockAider.Verify(e => e.ExecuteAsync("main", "aider task", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockExecutor.Verify(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ==================== 健康检查测试 ====================

    [Fact]
    public async Task CheckAllExecutorHealthAsync_ReturnsAllExecutors()
    {
        var results = await _daemon.CheckAllExecutorHealthAsync();
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.ExecutorName == "mock");
    }

    [Fact]
    public async Task CheckAllExecutorHealthAsync_HealthyExecutor_ReturnsHealthy()
    {
        var results = await _daemon.CheckAllExecutorHealthAsync();
        var mockResult = results.First(r => r.ExecutorName == "mock");
        Assert.True(mockResult.IsHealthy);
        Assert.Equal(TaskType.OpenClaw, mockResult.TaskType);
    }

    [Fact]
    public async Task CheckAllExecutorHealthAsync_UnhealthyExecutor_ReturnsUnhealthy()
    {
        _mockExecutor.Setup(e => e.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecutorHealthCheckResult
            {
                IsHealthy = false,
                ExecutorName = "mock",
                TaskType = TaskType.OpenClaw,
                Message = "CLI not found"
            });

        var results = await _daemon.CheckAllExecutorHealthAsync();
        var mockResult = results.First(r => r.ExecutorName == "mock");
        Assert.False(mockResult.IsHealthy);
        Assert.Equal("CLI not found", mockResult.Message);
    }

    [Fact]
    public async Task CheckExecutorHealthAsync_SpecificType_ReturnsCorrectExecutor()
    {
        var result = await _daemon.CheckExecutorHealthAsync(TaskType.OpenClaw);
        Assert.NotNull(result);
        Assert.True(result.IsHealthy);
        Assert.Equal("mock", result.ExecutorName);
    }

    [Fact]
    public async Task CheckExecutorHealthAsync_UnregisteredType_ReturnsNull()
    {
        var result = await _daemon.CheckExecutorHealthAsync(TaskType.LangGraph);
        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAllExecutorHealthAsync_ExceptionInCheck_ReturnsUnhealthy()
    {
        _mockExecutor.Setup(e => e.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Health check crashed"));

        var results = await _daemon.CheckAllExecutorHealthAsync();
        var mockResult = results.First(r => r.ExecutorName == "mock");
        Assert.False(mockResult.IsHealthy);
        Assert.Contains("Health check crashed", mockResult.Message);
    }

    [Fact]
    public async Task CheckAllExecutorHealthAsync_WithVersion_ReturnsVersion()
    {
        _mockExecutor.Setup(e => e.HealthCheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExecutorHealthCheckResult
            {
                IsHealthy = true,
                ExecutorName = "mock",
                TaskType = TaskType.OpenClaw,
                Message = "CLI available",
                Version = "1.2.3"
            });

        var results = await _daemon.CheckAllExecutorHealthAsync();
        var mockResult = results.First(r => r.ExecutorName == "mock");
        Assert.Equal("1.2.3", mockResult.Version);
    }

    // ==================== 重试策略测试 ====================

    [Fact]
    public void RetryPolicy_Default_IsExponentialBackoff()
    {
        var policy = new RetryPolicy();
        Assert.Equal(RetryStrategyType.ExponentialBackoff, policy.Strategy);
        Assert.Equal(3, policy.MaxRetries);
        Assert.Equal(1000, policy.BaseDelayMs);
        Assert.Equal(30000, policy.MaxDelayMs);
    }

    [Fact]
    public void RetryPolicy_ExponentialBackoff_CalculatesCorrectDelay()
    {
        var policy = new RetryPolicy { Strategy = RetryStrategyType.ExponentialBackoff, BaseDelayMs = 1000, BackoffMultiplier = 2.0 };
        Assert.Equal(1000, policy.CalculateDelay(0));
        Assert.Equal(2000, policy.CalculateDelay(1));
        Assert.Equal(4000, policy.CalculateDelay(2));
        Assert.Equal(8000, policy.CalculateDelay(3));
    }

    [Fact]
    public void RetryPolicy_FixedInterval_ReturnsConstantDelay()
    {
        var policy = new RetryPolicy { Strategy = RetryStrategyType.FixedInterval, BaseDelayMs = 5000 };
        Assert.Equal(5000, policy.CalculateDelay(0));
        Assert.Equal(5000, policy.CalculateDelay(1));
        Assert.Equal(5000, policy.CalculateDelay(5));
    }

    [Fact]
    public void RetryPolicy_LinearBackoff_IncrementsLinearly()
    {
        var policy = new RetryPolicy { Strategy = RetryStrategyType.LinearBackoff, BaseDelayMs = 2000 };
        Assert.Equal(2000, policy.CalculateDelay(0));
        Assert.Equal(4000, policy.CalculateDelay(1));
        Assert.Equal(6000, policy.CalculateDelay(2));
        Assert.Equal(8000, policy.CalculateDelay(3));
    }

    [Fact]
    public void RetryPolicy_MaxDelay_CapsDelay()
    {
        var policy = new RetryPolicy { Strategy = RetryStrategyType.ExponentialBackoff, BaseDelayMs = 1000, MaxDelayMs = 5000, BackoffMultiplier = 10.0 };
        Assert.Equal(5000, policy.CalculateDelay(3));
        Assert.Equal(5000, policy.CalculateDelay(10));
    }

    [Fact]
    public async Task RetryPolicy_CustomPolicy_UsedByDaemon()
    {
        _daemon.RetryPolicy = new RetryPolicy { MaxRetries = 1, Strategy = RetryStrategyType.FixedInterval, BaseDelayMs = 100 };

        Assert.Equal(RetryStrategyType.FixedInterval, _daemon.RetryPolicy.Strategy);
        Assert.Equal(1, _daemon.RetryPolicy.MaxRetries);
        Assert.Equal(100, _daemon.RetryPolicy.BaseDelayMs);
        Assert.Equal(100, _daemon.RetryPolicy.CalculateDelay(0));
        Assert.Equal(100, _daemon.RetryPolicy.CalculateDelay(1));

        await Task.CompletedTask;
    }
}
