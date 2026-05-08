using ClawPilot.Core.Models;

namespace ClawPilot.Core.Services;

public interface IExecutor
{
    TaskType SupportedTaskType { get; }

    string Name { get; }

    Task<ExecutorResult> ExecuteAsync(string agentName, string message, int timeoutSeconds, CancellationToken ct);

    Task<ExecutorHealthCheckResult> HealthCheckAsync(CancellationToken ct = default);
}

public class ExecutorResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
    public int ExitCode { get; init; }
}

public class ExecutorHealthCheckResult
{
    public bool IsHealthy { get; init; }
    public string ExecutorName { get; init; } = "";
    public TaskType TaskType { get; init; }
    public string Message { get; init; } = "";
    public string? Version { get; init; }
    public DateTime CheckedAt { get; init; } = DateTime.UtcNow;
}
