namespace ClawPilot.Core.Models;

/// <summary>
/// 自动驾驶编排目标
/// </summary>
public class AutopilotGoal
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// 编排白板 — 总体笔记和进度总结
/// </summary>
public class Whiteboard
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
    public int Version { get; set; } = 1;
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// 编排会话记录 — 每次唤醒的决策快照
/// </summary>
public class OrchestrationSession
{
    public int Id { get; set; }
    public string TriggeredAt { get; set; } = "";
    public string? CompletedAt { get; set; }
    public string DecisionSummary { get; set; } = "";
    public int TasksScheduled { get; set; }
    public int TasksSucceeded { get; set; }
    public int TasksFailed { get; set; }
    public string? WhiteboardBefore { get; set; }
    public string? WhiteboardAfter { get; set; }
    public string? RawDecisionJson { get; set; }
    public string Status { get; set; } = "pending"; // pending, completed, failed
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 自动驾驶决策输出（LLM 返回的 JSON 映射）
/// </summary>
public class AutopilotDecisionOutput
{
    public string DecisionType { get; set; } = "add_tasks";
    public string Reasoning { get; set; } = "";
    public List<AutopilotTaskToAdd> TasksToAdd { get; set; } = [];
    public string WhiteboardUpdate { get; set; } = "";
    public string? FuturePrediction { get; set; }
    public int? NextIntervalMinutes { get; set; }
}

/// <summary>
/// 自动驾驶待添加任务
/// </summary>
public class AutopilotTaskToAdd
{
    public string PersonaName { get; set; } = "";
    public string Message { get; set; } = "";
    public string TaskType { get; set; } = "openclaw";
    public string Priority { get; set; } = "normal";
    public string Reason { get; set; } = "";
    public int? DependsOnTaskId { get; set; }
    public string? ChainId { get; set; }
    public int ChainRound { get; set; } = 1;
}

/// <summary>
/// 审核结果 — 解析 reviewer 任务的输出
/// </summary>
public class ReviewResult
{
    public bool Passed { get; set; }
    public string Summary { get; set; } = "";
    public List<ReviewCheckItem> CheckItems { get; set; } = [];
    public List<string> Issues { get; set; } = [];
    public string? RawOutput { get; set; }
}

/// <summary>
/// 审核检查项
/// </summary>
public class ReviewCheckItem
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string? Detail { get; set; }
}

/// <summary>
/// 任务链状态 — 跟踪 coder→reviewer 闭环
/// </summary>
public class TaskChainState
{
    public string ChainId { get; set; } = "";
    public int CurrentRound { get; set; } = 1;
    public int MaxRounds { get; set; } = 3;
    public string Status { get; set; } = "active";
    public string? OriginalTaskMessage { get; set; }
    public string? LastReviewSummary { get; set; }
    public List<int> TaskIds { get; set; } = [];
}

/// <summary>
/// 自动驾驶运行时状态（供 UI 展示）
/// </summary>
public class AutopilotStatus
{
    public bool IsRunning { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public TimeSpan ElapsedSinceStart { get; set; }
    public string CurrentGoal { get; set; } = "";
    public string CurrentWhiteboardPreview { get; set; } = "";
    public int TotalSessions { get; set; }
    public int TotalTasksScheduled { get; set; }
    public string? LastError { get; set; }
}
