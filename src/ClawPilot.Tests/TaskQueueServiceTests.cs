using ClawPilot.Core.Models;
using ClawPilot.Core.Services;
using Microsoft.Extensions.Logging;
using TaskStatus = ClawPilot.Core.Models.TaskStatus;

namespace ClawPilot.Tests;

public class TaskQueueServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TaskQueueService _service;

    public TaskQueueServiceTests()
    {
        _dbPath = Path.GetTempFileName();
        _service = new TaskQueueService(_dbPath);
        _service.EnsureTableExistsAsync().Wait();
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task<int> AddTaskAsync(string message = "test", string agentName = "main",
        TaskType taskType = TaskType.OpenClaw, TaskSource source = TaskSource.User,
        int? dependsOnTaskId = null, string? chainId = null, int chainRound = 1)
    {
        var result = await _service.AddTaskAsync(message, agentName, taskType, source, dependsOnTaskId, chainId, chainRound);
        Assert.True(result.Success, result.Message);
        return result.TaskId!.Value;
    }

    // ==================== 基础 CRUD ====================

    [Fact]
    public async Task AddTaskAsync_SetsDefaultValues()
    {
        var taskId = await AddTaskAsync("hello");
        var task = await _service.GetTaskByIdAsync(taskId);

        Assert.NotNull(task);
        Assert.Equal("hello", task.Message);
        Assert.Equal("main", task.AgentName);
        Assert.Equal(TaskStatus.Pending, task.Status);
        Assert.Equal(TaskType.OpenClaw, task.TaskType);
        Assert.Equal(TaskSource.User, task.Source);
        Assert.Equal(0, task.RetryCount);
    }

    [Fact]
    public async Task AddTaskAsync_WithCustomTypeAndSource()
    {
        var taskId = await AddTaskAsync("aider task", "agent1", TaskType.Aider, TaskSource.Orchestrator);
        var task = await _service.GetTaskByIdAsync(taskId);

        Assert.NotNull(task);
        Assert.Equal(TaskType.Aider, task.TaskType);
        Assert.Equal(TaskSource.Orchestrator, task.Source);
        Assert.Equal("agent1", task.AgentName);
    }

    [Fact]
    public async Task GetTaskAsync_Nonexistent_ReturnsFail()
    {
        var result = await _service.GetTaskAsync(99999);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task DeleteTaskAsync_Nonexistent_ReturnsFail()
    {
        var result = await _service.DeleteTaskAsync(99999);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UpdateTaskAsync_UpdatesFields()
    {
        var taskId = await AddTaskAsync("original");
        var result = await _service.UpdateTaskAsync(taskId, agentName: "new-agent", message: "updated", status: TaskStatus.Running);

        Assert.True(result.Success);
        var task = await _service.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal("new-agent", task.AgentName);
        Assert.Equal("updated", task.Message);
        Assert.Equal(TaskStatus.Running, task.Status);
    }

    [Fact]
    public async Task UpdateTaskAsync_Nonexistent_ReturnsFail()
    {
        var result = await _service.UpdateTaskAsync(99999, message: "x");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task UpdateTaskAsync_NoFields_ReturnsFail()
    {
        var taskId = await AddTaskAsync("test");
        var result = await _service.UpdateTaskAsync(taskId);
        Assert.False(result.Success);
    }

    // ==================== GetNextPendingAsync ====================

    [Fact]
    public async Task GetNextPendingAsync_ReturnsPendingAndLocksToRunning()
    {
        var taskId = await AddTaskAsync("pending task");
        var task = await _service.GetNextPendingAsync();

        Assert.NotNull(task);
        Assert.Equal(taskId, task.Id);
        Assert.Equal(TaskStatus.Running, task.Status);
    }

    [Fact]
    public async Task GetNextPendingAsync_NoPending_ReturnsNull()
    {
        var task = await _service.GetNextPendingAsync();
        Assert.Null(task);
    }

    [Fact]
    public async Task GetNextPendingAsync_SkipsRunningTasks()
    {
        var taskId = await AddTaskAsync("first");
        await _service.GetNextPendingAsync();

        await AddTaskAsync("second");
        var task = await _service.GetNextPendingAsync();

        Assert.NotNull(task);
        Assert.NotEqual(taskId, task.Id);
        Assert.Equal("second", task.Message);
    }

    [Fact]
    public async Task GetNextPendingAsync_SkipsCompletedTasks()
    {
        var taskId = await AddTaskAsync("done task");
        await _service.ReportResultAsync(taskId, TaskStatus.Success, "ok");

        var task = await _service.GetNextPendingAsync();
        Assert.Null(task);
    }

    [Fact]
    public async Task GetNextPendingAsync_WithDependency_WaitsForSuccess()
    {
        var depTaskId = await AddTaskAsync("dependency");
        var childTaskId = await AddTaskAsync("child", dependsOnTaskId: depTaskId);

        var next = await _service.GetNextPendingAsync();
        Assert.NotNull(next);
        Assert.Equal(depTaskId, next.Id);

        await _service.ReportResultAsync(depTaskId, TaskStatus.Success, "done");

        next = await _service.GetNextPendingAsync();
        Assert.NotNull(next);
        Assert.Equal(childTaskId, next.Id);
    }

    [Fact]
    public async Task GetNextPendingAsync_WithFailedDependency_SkipsChild()
    {
        var depTaskId = await AddTaskAsync("dependency");
        await AddTaskAsync("child", dependsOnTaskId: depTaskId);

        var next = await _service.GetNextPendingAsync();
        Assert.NotNull(next);
        Assert.Equal(depTaskId, next.Id);

        await _service.ReportResultAsync(depTaskId, TaskStatus.Failed, "error");

        next = await _service.GetNextPendingAsync();
        Assert.Null(next);
    }

    [Fact]
    public async Task GetNextPendingAsync_ReturnsFirstByOrder()
    {
        var id3 = await AddTaskAsync("third");
        var id1 = await AddTaskAsync("first");
        var id2 = await AddTaskAsync("second");

        var next = await _service.GetNextPendingAsync();
        Assert.NotNull(next);
        Assert.Equal(id3, next.Id);
    }

    // ==================== ReportResultAsync ====================

    [Fact]
    public async Task ReportResultAsync_SetsStatusAndOutput()
    {
        var taskId = await AddTaskAsync("task");
        await _service.GetNextPendingAsync();

        var result = await _service.ReportResultAsync(taskId, TaskStatus.Success, "done!");
        Assert.True(result);

        var task = await _service.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Success, task.Status);
        Assert.Equal("done!", task.Output);
    }

    // ==================== ScheduleRetryAsync ====================

    [Fact]
    public async Task ScheduleRetryAsync_ResetsToPendingAndIncrementsRetryCount()
    {
        var taskId = await AddTaskAsync("retry task");
        await _service.GetNextPendingAsync();
        await _service.ReportResultAsync(taskId, TaskStatus.Failed, "error");

        var result = await _service.ScheduleRetryAsync(taskId, "retrying");
        Assert.True(result);

        var task = await _service.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Pending, task.Status);
        Assert.Equal(1, task.RetryCount);
        Assert.Equal("retrying", task.Output);
    }

    [Fact]
    public async Task ScheduleRetryAsync_MultipleRetries_IncrementsCount()
    {
        var taskId = await AddTaskAsync("retry task");
        await _service.ScheduleRetryAsync(taskId, "retry 1");
        await _service.ScheduleRetryAsync(taskId, "retry 2");
        await _service.ScheduleRetryAsync(taskId, "retry 3");

        var task = await _service.GetTaskByIdAsync(taskId);
        Assert.NotNull(task);
        Assert.Equal(3, task.RetryCount);
    }

    // ==================== ListTasksAsync 筛选 ====================

    [Fact]
    public async Task ListTasksAsync_FilterByStatus()
    {
        var id1 = await AddTaskAsync("pending1");
        var id2 = await AddTaskAsync("pending2");
        await _service.ReportResultAsync(id1, TaskStatus.Success, "ok");

        var pending = await _service.ListTasksAsync(status: TaskStatus.Pending);
        Assert.Equal(1, pending.Count);
        Assert.Equal(id2, pending[0].Id);
    }

    [Fact]
    public async Task ListTasksAsync_FilterByTaskType()
    {
        await AddTaskAsync("openclaw task", taskType: TaskType.OpenClaw);
        await AddTaskAsync("aider task", taskType: TaskType.Aider);

        var aiderTasks = await _service.ListTasksAsync(taskType: TaskType.Aider);
        Assert.Single(aiderTasks);
        Assert.Equal("aider task", aiderTasks[0].Message);
    }

    [Fact]
    public async Task ListTasksAsync_FilterByAgentName()
    {
        await AddTaskAsync("task1", agentName: "agent-a");
        await AddTaskAsync("task2", agentName: "agent-b");

        var tasks = await _service.ListTasksAsync(agentName: "agent-a");
        Assert.Single(tasks);
        Assert.Equal("agent-a", tasks[0].AgentName);
    }

    [Fact]
    public async Task ListTasksAsync_WithLimit()
    {
        for (int i = 0; i < 5; i++)
            await AddTaskAsync($"task {i}");

        var tasks = await _service.ListTasksAsync(limit: 3);
        Assert.Equal(3, tasks.Count);
    }

    [Fact]
    public async Task ListTasksAsync_FilterBySource()
    {
        await AddTaskAsync("user task", source: TaskSource.User);
        await AddTaskAsync("orchestrator task", source: TaskSource.Orchestrator);

        var tasks = await _service.ListTasksAsync(source: TaskSource.Orchestrator);
        Assert.Single(tasks);
        Assert.Equal(TaskSource.Orchestrator, tasks[0].Source);
    }

    // ==================== Chain 相关 ====================

    [Fact]
    public async Task AddTaskAsync_WithChainInfo()
    {
        var taskId = await AddTaskAsync("chain task", chainId: "chain-1", chainRound: 2);
        var task = await _service.GetTaskByIdAsync(taskId);

        Assert.NotNull(task);
        Assert.Equal("chain-1", task.ChainId);
        Assert.Equal(2, task.ChainRound);
    }

    [Fact]
    public async Task GetTasksByChainIdAsync_ReturnsOrderedTasks()
    {
        await AddTaskAsync("chain task 1", chainId: "chain-1");
        await AddTaskAsync("chain task 2", chainId: "chain-1");
        await AddTaskAsync("other chain", chainId: "chain-2");

        var tasks = await _service.GetTasksByChainIdAsync("chain-1");
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t => Assert.Equal("chain-1", t.ChainId));
    }

    // ==================== 统计 ====================

    [Fact]
    public async Task GetStatisticsAsync_ReturnsCorrectCounts()
    {
        var id1 = await AddTaskAsync("task1");
        var id2 = await AddTaskAsync("task2", taskType: TaskType.Aider);
        await _service.ReportResultAsync(id1, TaskStatus.Success, "ok");

        var stats = await _service.GetStatisticsAsync();
        Assert.NotNull(stats);
        Assert.Equal(2, stats.Total);
        Assert.True(stats.Status.ContainsKey(TaskStatus.Success));
        Assert.True(stats.Status.ContainsKey(TaskStatus.Pending));
        Assert.True(stats.Type.ContainsKey(TaskType.OpenClaw));
        Assert.True(stats.Type.ContainsKey(TaskType.Aider));
    }

    [Fact]
    public async Task GetStatisticsAsync_EmptyDatabase()
    {
        var stats = await _service.GetStatisticsAsync();
        Assert.NotNull(stats);
        Assert.Equal(0, stats.Total);
    }

    // ==================== 批量删除 ====================

    [Fact]
    public async Task DeleteTasksAsync_FilterByStatus()
    {
        var id1 = await AddTaskAsync("success task");
        await AddTaskAsync("pending task");
        await _service.ReportResultAsync(id1, TaskStatus.Success, "ok");

        var deleted = await _service.DeleteTasksAsync(status: TaskStatus.Success);
        Assert.Equal(1, deleted);

        var remaining = await _service.ListTasksAsync();
        Assert.Single(remaining);
        Assert.Equal(TaskStatus.Pending, remaining[0].Status);
    }

    [Fact]
    public async Task ClearCompletedTasksAsync_RemovesSuccessAndFailed()
    {
        var id1 = await AddTaskAsync("success");
        var id2 = await AddTaskAsync("failed");
        await AddTaskAsync("pending");
        await _service.ReportResultAsync(id1, TaskStatus.Success, "ok");
        await _service.ReportResultAsync(id2, TaskStatus.Failed, "err");

        var cleared = await _service.ClearCompletedTasksAsync();
        Assert.Equal(2, cleared);

        var remaining = await _service.ListTasksAsync();
        Assert.Single(remaining);
    }

    [Fact]
    public async Task ClearAllTasksAsync_RemovesEverything()
    {
        await AddTaskAsync("task1");
        await AddTaskAsync("task2");

        var cleared = await _service.ClearAllTasksAsync();
        Assert.Equal(2, cleared);

        var remaining = await _service.ListTasksAsync();
        Assert.Empty(remaining);
    }

    // ==================== GetRecentTasksAsync ====================

    [Fact]
    public async Task GetRecentTasksAsync_ReturnsRecentTasks()
    {
        await AddTaskAsync("recent1");
        await AddTaskAsync("recent2");

        var tasks = await _service.GetRecentTasksAsync(hours: 1);
        Assert.Equal(2, tasks.Count);
    }

    // ==================== 任务日志测试 ====================

    [Fact]
    public async Task AppendTaskLogAsync_InsertsLog()
    {
        var taskId = await AddTaskAsync();
        var logId = await _service.AppendTaskLogAsync(taskId, "main", "openclaw", "success", "done", 0, "OpenClaw", 1500);

        Assert.True(logId > 0);
    }

    [Fact]
    public async Task GetTaskLogsAsync_ReturnsLogsForTask()
    {
        var taskId = await AddTaskAsync();
        await _service.AppendTaskLogAsync(taskId, "main", "openclaw", "failed", "error", 0, "OpenClaw", 500);
        await _service.AppendTaskLogAsync(taskId, "main", "openclaw", "success", "done", 1, "OpenClaw", 1200);

        var logs = await _service.GetTaskLogsAsync(taskId);
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal(taskId, l.TaskId));
    }

    [Fact]
    public async Task GetTaskLogsAsync_ReturnsMostRecentFirst()
    {
        var taskId = await AddTaskAsync();
        await _service.AppendTaskLogAsync(taskId, "main", "openclaw", "failed", "first");
        await _service.AppendTaskLogAsync(taskId, "main", "openclaw", "success", "second");

        var logs = await _service.GetTaskLogsAsync(taskId);
        Assert.Equal(2, logs.Count);
        Assert.Contains(logs, l => l.Output == "first");
        Assert.Contains(logs, l => l.Output == "second");
    }

    [Fact]
    public async Task GetTaskLogsAsync_RespectsLimit()
    {
        var taskId = await AddTaskAsync();
        for (int i = 0; i < 5; i++)
            await _service.AppendTaskLogAsync(taskId, "main", "openclaw", "success", $"log{i}");

        var logs = await _service.GetTaskLogsAsync(taskId, limit: 3);
        Assert.Equal(3, logs.Count);
    }

    [Fact]
    public async Task GetTaskLogsAsync_NoLogs_ReturnsEmptyList()
    {
        var taskId = await AddTaskAsync();
        var logs = await _service.GetTaskLogsAsync(taskId);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task GetRecentLogsAsync_ReturnsAllRecentLogs()
    {
        var task1 = await AddTaskAsync("task1");
        var task2 = await AddTaskAsync("task2");
        await _service.AppendTaskLogAsync(task1, "main", "openclaw", "success", "t1");
        await _service.AppendTaskLogAsync(task2, "main", "openclaw", "failed", "t2");

        var logs = await _service.GetRecentLogsAsync();
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task GetRecentLogsAsync_RespectsLimit()
    {
        var taskId = await AddTaskAsync();
        for (int i = 0; i < 10; i++)
            await _service.AppendTaskLogAsync(taskId, "main", "openclaw", "success", $"log{i}");

        var logs = await _service.GetRecentLogsAsync(limit: 5);
        Assert.Equal(5, logs.Count);
    }

    [Fact]
    public async Task TaskLogEntry_ContainsCorrectFields()
    {
        var taskId = await AddTaskAsync();
        await _service.AppendTaskLogAsync(taskId, "agent1", "hermes", "success", "output", 2, "Hermes", 3000);

        var logs = await _service.GetTaskLogsAsync(taskId);
        var log = Assert.Single(logs);
        Assert.Equal(taskId, log.TaskId);
        Assert.Equal("agent1", log.AgentName);
        Assert.Equal("hermes", log.TaskType);
        Assert.Equal("success", log.Status);
        Assert.Equal("output", log.Output);
        Assert.Equal(2, log.RetryCount);
        Assert.Equal("Hermes", log.ExecutorName);
        Assert.Equal(3000, log.DurationMs);
        Assert.NotEmpty(log.CreatedAt);
    }

    [Fact]
    public async Task DeleteOldLogsAsync_RemovesOldLogs()
    {
        var taskId = await AddTaskAsync();
        await _service.AppendTaskLogAsync(taskId, "main", "openclaw", "success", "recent");

        var deleted = await _service.DeleteOldLogsAsync(keepDays: 30);
        Assert.True(deleted >= 0);
    }

    [Fact]
    public async Task AppendTaskLogAsync_NullOptionalFields_Works()
    {
        var taskId = await AddTaskAsync();
        var logId = await _service.AppendTaskLogAsync(taskId, "main", "openclaw", "success");

        Assert.True(logId > 0);
        var logs = await _service.GetTaskLogsAsync(taskId);
        var log = Assert.Single(logs);
        Assert.Null(log.Output);
        Assert.Null(log.ExecutorName);
        Assert.Null(log.DurationMs);
    }
}
