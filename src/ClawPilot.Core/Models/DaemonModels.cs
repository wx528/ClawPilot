namespace ClawPilot.Core.Models;

/// <summary>
/// Daemon 运行状态
/// </summary>
public class DaemonStatus
{
    public bool IsRunning { get; set; }
    public string? StartedAtIso { get; set; }
    public double? UptimeSeconds { get; set; }
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
