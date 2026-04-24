namespace ClawPilot.Core.Models;

/// <summary>
/// Persona — Agent 人设
/// </summary>
public class Persona
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string UserPromptPrefix { get; set; } = "";
    public string TaskType { get; set; } = "openclaw";
    public int MaxConcurrent { get; set; } = 1;
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public PersonaStatus Status { get; set; } = PersonaStatus.Active;
    public List<string> Tags { get; set; } = [];
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

/// <summary>
/// 提示词模板
/// </summary>
public class PromptTemplate
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Template { get; set; } = "";
    public List<string> Variables { get; set; } = [];
    public string DefaultAgent { get; set; } = "main";
    public string DefaultTaskType { get; set; } = "openclaw";
    public List<string> Tags { get; set; } = [];
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

/// <summary>
/// 计划项 — 一条待编排的任务指令
/// </summary>
public class PlanItem
{
    public string PersonaName { get; set; } = "";
    public string? PromptTemplateName { get; set; }
    public string Message { get; set; } = "";
    public string TaskType { get; set; } = "openclaw";
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public TimeSpan? ScheduledTime { get; set; }
    public List<int> DependsOn { get; set; } = [];
}

/// <summary>
/// 每日计划
/// </summary>
public class DailyPlan
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PlanItem> Items { get; set; } = [];
    public string ScheduleCron { get; set; } = "0 8 * * *";
    public string Timezone { get; set; } = "Asia/Shanghai";
    public PlanStatus Status { get; set; } = PlanStatus.Draft;
    public string? LastRunAt { get; set; }
    public string? NextRunAt { get; set; }
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

/// <summary>
/// LLM 编排决策结果
/// </summary>
public class OrchestrationDecision
{
    public DecisionType DecisionType { get; set; } = DecisionType.AddTasks;
    public string Reasoning { get; set; } = "";
    public List<TaskToAdd> TasksToAdd { get; set; } = [];
    public string DecisionModeUsed { get; set; } = "";
}

/// <summary>
/// 待添加任务（由 LLM 或规则引擎建议）
/// </summary>
public class TaskToAdd
{
    public string PersonaName { get; set; } = "";
    public string Message { get; set; } = "";
    public string TaskType { get; set; } = "openclaw";
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
    public string Reason { get; set; } = "";
}

/// <summary>
/// 编排上下文快照（传给 LLM 的当前状态）
/// </summary>
public class OrchestrationContext
{
    public DateTime Now { get; set; }
    public List<Persona> AvailablePersonas { get; set; } = [];
    public List<Persona> ActivePersonas { get; set; } = [];
    public List<TaskSnapshot> RecentTasks { get; set; } = [];
    public Dictionary<string, int> PersonaLoad { get; set; } = new();
    public int TotalPendingTasks { get; set; }
    public int TotalRunningTasks { get; set; }
    public int TotalTasksToday { get; set; }
    public string ProfileName { get; set; } = "";
}

/// <summary>
/// 任务快照（用于上下文构建）
/// </summary>
public class TaskSnapshot
{
    public int Id { get; set; }
    public string AgentName { get; set; } = "";
    public string Message { get; set; } = "";
    public TaskStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Output { get; set; }
}
