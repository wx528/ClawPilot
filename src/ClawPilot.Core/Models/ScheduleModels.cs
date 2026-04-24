namespace ClawPilot.Core.Models;

/// <summary>
/// 调度规格 - 用于定义任务的调度规则
/// </summary>
public class ScheduleSpec
{
    public string Id { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Cron { get; set; } = ""; // CRON 表达式
    public DateTime NextRun { get; set; } = DateTime.Now;
    public int Duration { get; set; } // 持续时间（秒）
    public List<object> Tasks { get; set; } = [];
    public int MinExecTime { get; set; } // 最小执行时间（秒）
    public int MaxExecTime { get; set; } = 3600; // 最大执行时间（秒）
    public int RetryCount { get; set; } = 0;
    public bool RandomizedDelay { get; set; } = false; // 是否随机化延迟
    public int RandomizedMax { get; set; } = 30; // 最大随机延迟（分钟）
    public bool Enabled { get; set; } = true;
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public string? Description { get; set; }
}

/// <summary>
/// YAML 配置 - 用于存储编排服务的配置
/// </summary>
public class YamlConfig
{
    public Dictionary<string, object>? ScheduleTasks { get; set; }
    public Dictionary<string, object>? LoopTasks { get; set; }
    public string? DefaultTimezone { get; set; } = "Asia/Shanghai";
    public int PollIntervalSeconds { get; set; } = 60;
    public bool EnableLogging { get; set; } = true;
    public string? LogPath { get; set; }
}