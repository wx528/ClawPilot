using ClawPilot.Core.Models;
using ClawPilot.Core.Services;

namespace ClawPilot.Tests.E2E;

public class MockExecutor : IExecutor
{
    public TaskType SupportedTaskType { get; set; } = TaskType.OpenClaw;
    public string Name { get; set; } = "mock-executor";
    public bool Succeeds { get; set; } = true;
    public string FixedOutput { get; set; } = "mock execution completed";
    public string FixedError { get; set; } = "";
    public int FixedExitCode { get; set; } = 0;
    public int ExecutionDelayMs { get; set; } = 0;
    public bool IsHealthy { get; set; } = true;
    public string HealthCheckMessage { get; set; } = "Mock executor is healthy";
    public int ExecutionCount { get; private set; }
    public List<ExecutorInvocation> Invocations { get; } = new();

    public async Task<ExecutorResult> ExecuteAsync(string agentName, string message, int timeoutSeconds, CancellationToken ct)
    {
        ExecutionCount++;
        Invocations.Add(new ExecutorInvocation
        {
            AgentName = agentName,
            Message = message,
            TimeoutSeconds = timeoutSeconds,
            InvokedAt = DateTime.UtcNow
        });

        if (ExecutionDelayMs > 0)
            await Task.Delay(ExecutionDelayMs, ct);

        return new ExecutorResult
        {
            Success = Succeeds,
            Output = FixedOutput,
            Error = FixedError,
            ExitCode = FixedExitCode
        };
    }

    public Task<ExecutorHealthCheckResult> HealthCheckAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ExecutorHealthCheckResult
        {
            IsHealthy = IsHealthy,
            ExecutorName = Name,
            TaskType = SupportedTaskType,
            Message = HealthCheckMessage
        });
    }
}

public class ExecutorInvocation
{
    public string AgentName { get; set; } = "";
    public string Message { get; set; } = "";
    public int TimeoutSeconds { get; set; }
    public DateTime InvokedAt { get; set; }
}
