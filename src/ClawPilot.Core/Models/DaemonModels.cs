namespace ClawPilot.Core.Models;

public enum RetryStrategyType
{
    ExponentialBackoff,
    FixedInterval,
    LinearBackoff
}

public class RetryPolicy
{
    public int MaxRetries { get; set; } = 3;
    public RetryStrategyType Strategy { get; set; } = RetryStrategyType.ExponentialBackoff;
    public int BaseDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 30000;
    public double BackoffMultiplier { get; set; } = 2.0;

    public int CalculateDelay(int retryCount)
    {
        var delay = Strategy switch
        {
            RetryStrategyType.FixedInterval => (long)BaseDelayMs,
            RetryStrategyType.LinearBackoff => (long)BaseDelayMs * (retryCount + 1),
            RetryStrategyType.ExponentialBackoff => (long)(BaseDelayMs * Math.Pow(BackoffMultiplier, retryCount)),
            _ => (long)BaseDelayMs
        };
        return (int)Math.Min(delay, MaxDelayMs);
    }
}

/// <summary>
/// Daemon 运行状态
/// </summary>
public class DaemonStatus
{
    public bool IsRunning { get; set; }
    public string? StartedAtIso { get; set; }
    public double? UptimeSeconds { get; set; }
    public int ActiveTaskCount { get; set; }
    public int MaxConcurrency { get; set; }
    public int StatsProcessed { get; set; }
    public int StatsSucceeded { get; set; }
    public int StatsFailed { get; set; }
    public Dictionary<string, string> CurrentTaskInfo { get; set; } = new();
    public List<Dictionary<string, object>> ExecutionHistory { get; set; } = [];
    public List<string> RegisteredExecutors { get; set; } = [];

    public string UptimeText
    {
        get
        {
            if (!UptimeSeconds.HasValue) return "--";
            var ts = TimeSpan.FromSeconds(UptimeSeconds.Value);
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }
    }
}

public class TaskLogEntry
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string AgentName { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Output { get; set; }
    public int RetryCount { get; set; }
    public string? ExecutorName { get; set; }
    public long? DurationMs { get; set; }
    public string CreatedAt { get; set; } = "";
}
