using ClawPilot.Core.Models;
using ClawPilot.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;
using TaskStatus = ClawPilot.Core.Models.TaskStatus;

namespace ClawPilot.Tests.E2E;

public class OrchestrationE2ETests : IAsyncLifetime
{
    private readonly MockLlmServer _llmServer;
    private readonly string _dbPath;
    private readonly TaskQueueService _taskQueue;
    private readonly OrchestratorStorageService _storage;
    private readonly OpenAILlmClient _llmClient;
    private readonly LlmDecisionEngine _llmEngine;
    private readonly MockExecutor _mockOpenClawExecutor;
    private readonly MockExecutor _mockHermesExecutor;
    private readonly ExecutorRegistry _registry;
    private readonly DaemonService _daemon;
    private readonly AutopilotOrchestrator _orchestrator;

    public OrchestrationE2ETests()
    {
        _llmServer = new MockLlmServer();
        _dbPath = Path.GetTempFileName();

        _taskQueue = new TaskQueueService(_dbPath);
        _taskQueue.EnsureTableExistsAsync().Wait();

        _storage = new OrchestratorStorageService(_dbPath);
        _storage.EnsureTablesExistAsync().Wait();

        _llmClient = new OpenAILlmClient("test-key", _llmServer.BaseUrl + "/v1", "mock-model");
        _llmEngine = new LlmDecisionEngine(_llmClient);

        _mockOpenClawExecutor = new MockExecutor
        {
            SupportedTaskType = TaskType.OpenClaw,
            Name = "mock-openclaw",
            FixedOutput = "Task completed successfully by mock executor"
        };

        _mockHermesExecutor = new MockExecutor
        {
            SupportedTaskType = TaskType.Hermes,
            Name = "mock-hermes",
            FixedOutput = "Review completed: PASS"
        };

        _registry = new ExecutorRegistry(
            new OpenClawExecutor("openclaw"),
            new HermesExecutor(Mock.Of<ILogger<HermesExecutor>>(), "nonexistent.ps1"),
            new KimiCodeExecutor(Mock.Of<ILogger<KimiCodeExecutor>>(), "nonexistent-kimi"),
            new CodeBuddyExecutor(Mock.Of<ILogger<CodeBuddyExecutor>>(), "nonexistent-cb"),
            new AiderExecutor(Mock.Of<ILogger<AiderExecutor>>(), "nonexistent-aider"),
            new CodexExecutor(Mock.Of<ILogger<CodexExecutor>>(), "nonexistent-codex"),
            new QwenCodeExecutor(Mock.Of<ILogger<QwenCodeExecutor>>(), "nonexistent-qwen"));

        _registry.Register(_mockOpenClawExecutor);
        _registry.Register(_mockHermesExecutor);

        _daemon = new DaemonService(_taskQueue, _registry);
        _daemon.PollIntervalSeconds = 1;
        _daemon.MaxConcurrency = 5;
        _daemon.MaxRetries = 0;

        _orchestrator = new AutopilotOrchestrator(_taskQueue, _storage, _llmEngine);
        _orchestrator.Interval = TimeSpan.FromMinutes(60);
        _orchestrator.AgentName = "main";
        _orchestrator.ExecutorType = ExecutorType.Auto;
    }

    public async Task InitializeAsync()
    {
        await _llmServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _orchestrator.Stop();
        _daemon.Stop();
        await _llmServer.StopAsync();
        await _llmServer.DisposeAsync();
        _llmClient.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task SetupGoalAsync(string title = "E2E Test Goal", string description = "Test goal for E2E testing")
    {
        await _storage.CreateGoalAsync(title, description);
    }

    // ==================== 基础编排循环 ====================

    [Fact]
    public async Task BasicOrchestrationLoop_LlmDecides_DaemonExecutes()
    {
        await SetupGoalAsync("Implement feature X");

        _llmServer.SetDecisionResponse(
            new AutopilotDecisionBuilder()
                .SetReasoning("Need to implement feature X")
                .AddTask("Implement feature X in module A", "main", "openclaw")
                .SetWhiteboardUpdate("Feature X implementation started")
        );

        var cycleResult = await _orchestrator.ExecuteCycleOnceAsync();
        Assert.True(cycleResult, "Orchestration cycle should succeed");

        var pendingTasks = await _taskQueue.ListTasksAsync(status: TaskStatus.Pending);
        Assert.Single(pendingTasks);
        Assert.Equal("Implement feature X in module A", pendingTasks[0].Message);
        Assert.Equal(TaskSource.Orchestrator, pendingTasks[0].Source);

        var daemonResult = await _daemon.RunOnceAsync();
        Assert.True(daemonResult, "Daemon should process the task");

        var task = await _taskQueue.GetTaskByIdAsync(pendingTasks[0].Id);
        Assert.NotNull(task);
        Assert.Equal(TaskStatus.Success, task.Status);
        Assert.Contains("mock executor", task.Output);

        Assert.Equal(1, _mockOpenClawExecutor.ExecutionCount);
        Assert.Equal(1, _daemon.StatsProcessed);
        Assert.Equal(1, _daemon.StatsSucceeded);
    }

    [Fact]
    public async Task MultiTaskOrchestration_AllExecutedInOrder()
    {
        await SetupGoalAsync("Multi-step task");

        _llmServer.SetDecisionResponse(
            new AutopilotDecisionBuilder()
                .AddTask("Step 1: Analyze codebase", "main", "openclaw", priority: "high")
                .AddTask("Step 2: Write tests", "main", "openclaw")
                .SetWhiteboardUpdate("Multi-step plan created")
        );

        await _orchestrator.ExecuteCycleOnceAsync();

        var allTasks = await _taskQueue.ListTasksAsync();
        Assert.Equal(2, allTasks.Count);

        await _daemon.RunOnceAsync();
        await _daemon.RunOnceAsync();

        var completed = await _taskQueue.ListTasksAsync(status: TaskStatus.Success);
        Assert.Equal(2, completed.Count);
        Assert.Equal(2, _mockOpenClawExecutor.ExecutionCount);
    }

    // ==================== 闭环编排 ====================

    [Fact]
    public async Task ClosedLoopOrchestration_CoderThenReviewer()
    {
        await SetupGoalAsync("Code with review");

        _llmServer.SetDecisionResponse(
            new AutopilotDecisionBuilder()
                .AddTask("Write the login module", "main", "openclaw", chainId: "chain-login", chainRound: 1)
                .SetWhiteboardUpdate("Login module task created")
        );

        await _orchestrator.ExecuteCycleOnceAsync();

        var coderTask = (await _taskQueue.ListTasksAsync(status: TaskStatus.Pending))[0];
        Assert.Equal("chain-login", coderTask.ChainId);
        Assert.Equal(1, coderTask.ChainRound);

        _mockOpenClawExecutor.FixedOutput = "Login module implemented successfully";
        await _daemon.RunOnceAsync();

        coderTask = await _taskQueue.GetTaskByIdAsync(coderTask.Id);
        Assert.NotNull(coderTask);
        Assert.Equal(TaskStatus.Success, coderTask.Status);

        _llmServer.SetDecisionResponse(
            new AutopilotDecisionBuilder()
                .AddTask("Review the login module code", "reviewer", "hermes",
                    reason: "Review coder output", dependsOnTaskId: coderTask.Id,
                    chainId: "chain-login", chainRound: 2)
                .SetWhiteboardUpdate("Review task scheduled")
        );

        await _orchestrator.ExecuteCycleOnceAsync();

        var reviewerTask = (await _taskQueue.ListTasksAsync(status: TaskStatus.Pending))
            .FirstOrDefault(t => t.ChainId == "chain-login" && t.ChainRound == 2);
        Assert.NotNull(reviewerTask);

        _mockHermesExecutor.FixedOutput = "REVIEW_RESULT: PASS\nSummary: Code quality is good\nIssues: None";
        await _daemon.RunOnceAsync();

        reviewerTask = await _taskQueue.GetTaskByIdAsync(reviewerTask.Id);
        Assert.NotNull(reviewerTask);
        Assert.Equal(TaskStatus.Success, reviewerTask.Status);
        Assert.Contains("PASS", reviewerTask.Output);
    }

    // ==================== 执行器路由 ====================

    [Fact]
    public async Task ExecutorRouting_DifferentTaskTypes_RouteToCorrectExecutor()
    {
        await SetupGoalAsync("Multi-executor task");

        _llmServer.SetDecisionResponse(
            new AutopilotDecisionBuilder()
                .AddTask("OpenClaw task", "main", "openclaw")
                .AddTask("Hermes task", "main", "hermes")
                .SetWhiteboardUpdate("Multi-executor tasks")
        );

        await _orchestrator.ExecuteCycleOnceAsync();

        await _daemon.RunOnceAsync();
        await _daemon.RunOnceAsync();

        Assert.Equal(1, _mockOpenClawExecutor.ExecutionCount);
        Assert.Equal(1, _mockHermesExecutor.ExecutionCount);

        var openClawInvocation = _mockOpenClawExecutor.Invocations[0];
        Assert.Equal("OpenClaw task", openClawInvocation.Message);

        var hermesInvocation = _mockHermesExecutor.Invocations[0];
        Assert.Equal("Hermes task", hermesInvocation.Message);
    }

    // ==================== 执行器失败处理 ====================

    [Fact]
    public async Task ExecutorFailure_TaskMarkedFailed_StatsUpdated()
    {
        await SetupGoalAsync("Failing task");

        _llmServer.SetDecisionResponse(
            new AutopilotDecisionBuilder()
                .AddTask("This will fail", "main", "openclaw")
        );

        await _orchestrator.ExecuteCycleOnceAsync();

        _mockOpenClawExecutor.Succeeds = false;
        _mockOpenClawExecutor.FixedOutput = "Execution failed";
        _mockOpenClawExecutor.FixedError = "Command not found";
        _mockOpenClawExecutor.FixedExitCode = 1;

        await _daemon.RunOnceAsync();

        var tasks = await _taskQueue.ListTasksAsync(status: TaskStatus.Failed);
        Assert.Single(tasks);
        Assert.Equal(1, _daemon.StatsFailed);
    }

    // ==================== LLM 交互验证 ====================

    [Fact]
    public async Task LlmInteraction_ReceivesCorrectContext()
    {
        await SetupGoalAsync("Context verification goal");
        await _storage.UpdateWhiteboardAsync("Initial whiteboard state");

        _llmServer.SetDecisionResponse(
            new AutopilotDecisionBuilder()
                .AddTask("Context task", "main", "openclaw")
        );

        await _orchestrator.ExecuteCycleOnceAsync();

        Assert.Single(_llmServer.ReceivedRequests);
        var request = _llmServer.ReceivedRequests[0];
        Assert.Contains("Context verification goal", request.UserPrompt);
        Assert.Contains("autopilot", request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LlmDecision_NoTasks_WhiteboardOnly()
    {
        await SetupGoalAsync("Observation only");

        _llmServer.SetDecisionResponse(
            new AutopilotDecisionBuilder()
                .SetDecisionType("observe")
                .SetReasoning("No action needed right now")
                .SetWhiteboardUpdate("Observation: everything looks good")
        );

        await _orchestrator.ExecuteCycleOnceAsync();

        var tasks = await _taskQueue.ListTasksAsync();
        Assert.Empty(tasks);

        var whiteboard = await _storage.GetLatestWhiteboardAsync();
        Assert.Contains("Observation: everything looks good", whiteboard.Content);
    }

    // ==================== 统计与状态 ====================

    [Fact]
    public async Task DaemonStatus_ReflectsExecutionHistory()
    {
        await SetupGoalAsync("Status check");

        _llmServer.SetDecisionResponse(
            new AutopilotDecisionBuilder()
                .AddTask("Task for status check", "main", "openclaw")
        );

        await _orchestrator.ExecuteCycleOnceAsync();
        await _daemon.RunOnceAsync();

        var status = _daemon.GetStatus();
        Assert.Equal(1, status.StatsProcessed);
        Assert.Equal(1, status.StatsSucceeded);
        Assert.Equal(0, status.StatsFailed);
        Assert.Single(status.ExecutionHistory);
        Assert.Contains("mock-openclaw", status.RegisteredExecutors);
    }

    // ==================== 真实 API 可选模式 ====================

    [Fact(Skip = "需要真实 API Key，设置环境变量 CLAWPILOT_E2E_REAL_API=1 启用")]
    public async Task RealApiE2E_FullOrchestrationLoop()
    {
        var apiKey = Environment.GetEnvironmentVariable("CLAWPILOT_OPENAI_API_KEY");
        var baseUrl = Environment.GetEnvironmentVariable("CLAWPILOT_OPENAI_BASE_URL") ?? "https://api.openai.com/v1";
        var model = Environment.GetEnvironmentVariable("CLAWPILOT_OPENAI_MODEL") ?? "gpt-4o-mini";

        if (string.IsNullOrEmpty(apiKey))
            return;

        var realLlmClient = new OpenAILlmClient(apiKey, baseUrl, model);
        var realLlmEngine = new LlmDecisionEngine(realLlmClient);
        var realOrchestrator = new AutopilotOrchestrator(_taskQueue, _storage, realLlmEngine);

        await SetupGoalAsync("Write a simple hello world function in Python");

        await realOrchestrator.ExecuteCycleOnceAsync();

        var tasks = await _taskQueue.ListTasksAsync(status: TaskStatus.Pending);
        Assert.NotEmpty(tasks);

        await _daemon.RunOnceAsync();

        var completed = await _taskQueue.ListTasksAsync(status: TaskStatus.Success);
        Assert.NotEmpty(completed);

        realLlmClient.Dispose();
    }
}
