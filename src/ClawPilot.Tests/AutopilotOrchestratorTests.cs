using ClawPilot.Core.Models;
using ClawPilot.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace ClawPilot.Tests;

public class AutopilotOrchestratorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Mock<ILlmClient> _mockLlmClient;
    private readonly TaskQueueService _taskQueue;
    private readonly OrchestratorStorageService _storage;
    private readonly LlmDecisionEngine _llmEngine;
    private readonly AutopilotOrchestrator _orchestrator;

    public AutopilotOrchestratorTests()
    {
        _dbPath = Path.GetTempFileName();
        _mockLlmClient = new Mock<ILlmClient>();

        var tqLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<TaskQueueService>();
        _taskQueue = new TaskQueueService(_dbPath, tqLogger);

        var storageLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger<OrchestratorStorageService>();
        _storage = new OrchestratorStorageService(_dbPath, storageLogger);

        _llmEngine = new LlmDecisionEngine(_mockLlmClient.Object);
        _orchestrator = new AutopilotOrchestrator(_taskQueue, _storage, _llmEngine);
    }

    public void Dispose()
    {
        _orchestrator.Stop();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task InitStorageAsync()
    {
        await _storage.EnsureTablesExistAsync();
        await _taskQueue.EnsureTableExistsAsync();
    }

    private async Task CreateActiveGoalAsync(string title = "Test Goal", string description = "Test")
    {
        await _storage.CreateGoalAsync(title, description);
    }

    private void SetupLlmResponse(int taskCount = 1, string whiteboardUpdate = "Updated whiteboard")
    {
        var tasks = new List<string>();
        for (int i = 0; i < taskCount; i++)
        {
            tasks.Add($@"{{""persona_name"":""main"",""message"":""Task {i + 1}"",""task_type"":""openclaw"",""priority"":""normal"",""reason"":""test""}}");
        }

        var response = $@"{{
            ""decision_type"": ""add_tasks"",
            ""reasoning"": ""Test reasoning"",
            ""tasks_to_add"": [{string.Join(",", tasks)}],
            ""whiteboard_update"": ""{whiteboardUpdate}""
        }}";

        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
    }

    private void SetupEmptyLlmResponse()
    {
        var response = @"{
            ""decision_type"": ""add_tasks"",
            ""reasoning"": ""No tasks needed"",
            ""tasks_to_add"": [],
            ""whiteboard_update"": ""No changes""
        }";

        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
    }

    // ==================== 生命周期 ====================

    [Fact]
    public async Task StartAsync_SetsIsRunning()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync();
        SetupLlmResponse();

        _orchestrator.StartAsync(TimeSpan.FromSeconds(10));

        Assert.True(_orchestrator.IsRunning);

        _orchestrator.Stop();
    }

    [Fact]
    public void Stop_SetsIsRunningFalse()
    {
        _orchestrator.Stop();
        Assert.False(_orchestrator.IsRunning);
    }

    [Fact]
    public async Task StartAsync_DoubleStart_DoesNotThrow()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync();
        SetupLlmResponse();

        _orchestrator.StartAsync(TimeSpan.FromMinutes(10));
        _orchestrator.StartAsync(TimeSpan.FromMinutes(10));

        Assert.True(_orchestrator.IsRunning);
        _orchestrator.Stop();
    }

    // ==================== 手动触发 ====================

    [Fact]
    public async Task TriggerNowAsync_WhenRunning_ExecutesCycle()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync();
        SetupLlmResponse(taskCount: 2);

        _orchestrator.StartAsync(TimeSpan.FromHours(1));
        await _orchestrator.TriggerNowAsync();

        var sessions = await _storage.ListSessionsAsync();
        Assert.True(sessions.Count >= 1);

        _orchestrator.Stop();
    }

    [Fact]
    public async Task TriggerNowAsync_WhenNotRunning_DoesNotExecute()
    {
        await InitStorageAsync();

        await _orchestrator.TriggerNowAsync();

        var sessions = await _storage.ListSessionsAsync();
        Assert.Empty(sessions);
    }

    // ==================== 编排周期 ====================

    [Fact]
    public async Task ExecuteCycle_WithTasks_RecordsSession()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync();
        SetupLlmResponse(taskCount: 3, whiteboardUpdate: "3 tasks scheduled");

        _orchestrator.StartAsync(TimeSpan.FromHours(1));
        await Task.Delay(500);
        await _orchestrator.TriggerNowAsync();
        await Task.Delay(500);

        var sessions = await _storage.ListSessionsAsync();
        Assert.True(sessions.Count >= 1);

        var completedSession = sessions.FirstOrDefault(s => s.Status == "completed");
        if (completedSession != null)
        {
            Assert.Equal(3, completedSession.TasksScheduled);
        }

        _orchestrator.Stop();
    }

    [Fact]
    public async Task ExecuteCycle_NoGoal_FailsSession()
    {
        await InitStorageAsync();
        SetupLlmResponse();

        _orchestrator.StartAsync(TimeSpan.FromHours(1));
        await Task.Delay(500);
        await _orchestrator.TriggerNowAsync();
        await Task.Delay(500);

        var sessions = await _storage.ListSessionsAsync();
        var failedSession = sessions.FirstOrDefault(s => s.Status == "failed");
        if (failedSession != null)
        {
            Assert.Contains("没有活动的编排目标", failedSession.ErrorMessage);
        }

        _orchestrator.Stop();
    }

    [Fact]
    public async Task ExecuteCycle_LlmReturnsNull_FailsSession()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync();

        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Not valid JSON without braces");

        _orchestrator.StartAsync(TimeSpan.FromHours(1));
        await Task.Delay(500);
        await _orchestrator.TriggerNowAsync();
        await Task.Delay(500);

        var sessions = await _storage.ListSessionsAsync();
        var failedSession = sessions.FirstOrDefault(s => s.Status == "failed");
        if (failedSession != null)
        {
            Assert.NotNull(failedSession.ErrorMessage);
        }

        _orchestrator.Stop();
    }

    // ==================== 空周期回退 ====================

    [Fact]
    public async Task ExecuteCycle_ConsecutiveEmptyCycles_TriggersFallback()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync();
        SetupEmptyLlmResponse();
        _orchestrator.EmptyCycleThreshold = 2;

        _orchestrator.StartAsync(TimeSpan.FromHours(1));

        for (int i = 0; i < 3; i++)
        {
            await _orchestrator.TriggerNowAsync();
            await Task.Delay(300);
        }

        var sessions = await _storage.ListSessionsAsync();
        var completedSessions = sessions.Where(s => s.Status == "completed").ToList();

        var lastSession = completedSessions.FirstOrDefault();
        if (lastSession != null)
        {
            Assert.True(lastSession.TasksScheduled >= 1);
        }

        _orchestrator.Stop();
    }

    // ==================== 白板更新 ====================

    [Fact]
    public async Task ExecuteCycle_UpdatesWhiteboard()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync();
        SetupLlmResponse(taskCount: 1, whiteboardUpdate: "Mission progress updated");

        _orchestrator.StartAsync(TimeSpan.FromHours(1));
        await Task.Delay(500);
        await _orchestrator.TriggerNowAsync();
        await Task.Delay(500);

        var whiteboard = await _storage.GetLatestWhiteboardAsync();
        Assert.Equal("Mission progress updated", whiteboard.Content);

        _orchestrator.Stop();
    }

    // ==================== 自适应间隔 ====================

    [Fact]
    public async Task ExecuteCycle_AdaptiveInterval_UpdatesInterval()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync();
        _orchestrator.AdaptiveIntervalEnabled = true;

        var response = @"{
            ""decision_type"": ""add_tasks"",
            ""reasoning"": ""test"",
            ""tasks_to_add"": [{""persona_name"":""main"",""message"":""task"",""task_type"":""openclaw"",""priority"":""normal"",""reason"":""test""}],
            ""whiteboard_update"": ""updated"",
            ""next_interval_minutes"": 30
        }";

        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        _orchestrator.StartAsync(TimeSpan.FromHours(1));
        await Task.Delay(500);
        await _orchestrator.TriggerNowAsync();
        await Task.Delay(500);

        Assert.Equal(TimeSpan.FromMinutes(30), _orchestrator.Interval);

        _orchestrator.Stop();
    }

    [Fact]
    public async Task ExecuteCycle_AdaptiveInterval_IgnoresOutOfRange()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync();
        _orchestrator.AdaptiveIntervalEnabled = true;
        var originalInterval = TimeSpan.FromHours(1);

        var response = @"{
            ""decision_type"": ""add_tasks"",
            ""reasoning"": ""test"",
            ""tasks_to_add"": [{""persona_name"":""main"",""message"":""task"",""task_type"":""openclaw"",""priority"":""normal"",""reason"":""test""}],
            ""whiteboard_update"": ""updated"",
            ""next_interval_minutes"": 1
        }";

        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        _orchestrator.StartAsync(originalInterval);
        await Task.Delay(500);
        await _orchestrator.TriggerNowAsync();
        await Task.Delay(500);

        Assert.Equal(originalInterval, _orchestrator.Interval);

        _orchestrator.Stop();
    }

    // ==================== ResolveTaskType ====================

    [Fact]
    public void ResolveTaskType_OpenClaw_ReturnsOpenClaw()
    {
        var result = InvokeResolveTaskType(null, ExecutorType.OpenClaw);
        Assert.Equal(TaskType.OpenClaw, result);
    }

    [Fact]
    public void ResolveTaskType_Hermes_ReturnsHermes()
    {
        var result = InvokeResolveTaskType(null, ExecutorType.Hermes);
        Assert.Equal(TaskType.Hermes, result);
    }

    [Fact]
    public void ResolveTaskType_KimiCode_ReturnsKimiCode()
    {
        var result = InvokeResolveTaskType(null, ExecutorType.KimiCode);
        Assert.Equal(TaskType.KimiCode, result);
    }

    [Fact]
    public void ResolveTaskType_CodeBuddy_ReturnsCodeBuddy()
    {
        var result = InvokeResolveTaskType(null, ExecutorType.CodeBuddy);
        Assert.Equal(TaskType.CodeBuddy, result);
    }

    [Fact]
    public void ResolveTaskType_AutoWithLlmHermes_ReturnsHermes()
    {
        var result = InvokeResolveTaskType("hermes", ExecutorType.Auto);
        Assert.Equal(TaskType.Hermes, result);
    }

    [Fact]
    public void ResolveTaskType_AutoWithLlmKimi_ReturnsKimiCode()
    {
        var result = InvokeResolveTaskType("kimicode", ExecutorType.Auto);
        Assert.Equal(TaskType.KimiCode, result);
    }

    [Fact]
    public void ResolveTaskType_AutoWithLlmCodeBuddy_ReturnsCodeBuddy()
    {
        var result = InvokeResolveTaskType("codebuddy", ExecutorType.Auto);
        Assert.Equal(TaskType.CodeBuddy, result);
    }

    [Fact]
    public void ResolveTaskType_AutoWithUnknown_DefaultsToOpenClaw()
    {
        var result = InvokeResolveTaskType("unknown_tool", ExecutorType.Auto);
        Assert.Equal(TaskType.OpenClaw, result);
    }

    [Fact]
    public void ResolveTaskType_Aider_ReturnsAider()
    {
        var result = InvokeResolveTaskType(null, ExecutorType.Aider);
        Assert.Equal(TaskType.Aider, result);
    }

    [Fact]
    public void ResolveTaskType_Codex_ReturnsCodex()
    {
        var result = InvokeResolveTaskType(null, ExecutorType.Codex);
        Assert.Equal(TaskType.Codex, result);
    }

    [Fact]
    public void ResolveTaskType_QwenCode_ReturnsQwenCode()
    {
        var result = InvokeResolveTaskType(null, ExecutorType.QwenCode);
        Assert.Equal(TaskType.QwenCode, result);
    }

    [Fact]
    public void ResolveTaskType_AutoWithLlmAider_ReturnsAider()
    {
        var result = InvokeResolveTaskType("aider", ExecutorType.Auto);
        Assert.Equal(TaskType.Aider, result);
    }

    [Fact]
    public void ResolveTaskType_AutoWithLlmCodex_ReturnsCodex()
    {
        var result = InvokeResolveTaskType("codex", ExecutorType.Auto);
        Assert.Equal(TaskType.Codex, result);
    }

    [Fact]
    public void ResolveTaskType_AutoWithLlmQwenCode_ReturnsQwenCode()
    {
        var result = InvokeResolveTaskType("qwencode", ExecutorType.Auto);
        Assert.Equal(TaskType.QwenCode, result);
    }

    [Fact]
    public void ResolveTaskType_AutoWithLlmQwen_ReturnsQwenCode()
    {
        var result = InvokeResolveTaskType("qwen", ExecutorType.Auto);
        Assert.Equal(TaskType.QwenCode, result);
    }

    [Fact]
    public void ResolveTaskType_AutoWithLlmQwenCodeDash_ReturnsQwenCode()
    {
        var result = InvokeResolveTaskType("qwen-code", ExecutorType.Auto);
        Assert.Equal(TaskType.QwenCode, result);
    }

    private static TaskType InvokeResolveTaskType(string? llmTaskType, ExecutorType configuredType)
    {
        var method = typeof(AutopilotOrchestrator).GetMethod("ResolveTaskType",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (TaskType)method!.Invoke(null, new object?[] { llmTaskType, configuredType })!;
    }

    // ==================== 状态查询 ====================

    [Fact]
    public async Task GetStatusAsync_ReturnsCurrentState()
    {
        await InitStorageAsync();
        await CreateActiveGoalAsync("My Mission");

        var status = await _orchestrator.GetStatusAsync();

        Assert.False(status.IsRunning);
        Assert.Equal("My Mission", status.CurrentGoal);
    }

    [Fact]
    public async Task GetStatusAsync_NoGoal_ShowsUnset()
    {
        await InitStorageAsync();

        var status = await _orchestrator.GetStatusAsync();

        Assert.Equal("(未设置)", status.CurrentGoal);
    }
}
