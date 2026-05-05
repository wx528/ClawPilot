namespace ClawPilot.Core.Models;

/// <summary>
/// 任务状态枚举
/// </summary>
public enum TaskStatus
{
    Pending,
    Running,
    Success,
    Failed
}

/// <summary>
/// 任务类型
/// </summary>
public enum TaskType
{
    OpenClaw,
    LangGraph,
    Hermes,
    KimiCode,
    CodeBuddy
}

/// <summary>
/// 任务来源
/// </summary>
public enum TaskSource
{
    User,
    Orchestrator,
    Cli,
    Mcp,
    Reviewer
}

/// <summary>
/// 任务优先级
/// </summary>
public enum TaskPriority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum ExecutorType
{
    OpenClaw,
    Hermes,
    KimiCode,
    CodeBuddy,
    Auto
}

/// <summary>
/// 自动驾驶模式
/// </summary>
public enum AutopilotMode
{
    PlanAndExecute,
    ReAct
}

/// <summary>
/// 编排草案状态
/// </summary>
public enum DraftStatus
{
    Pending,
    Approved,
    Executing,
    Executed,
    Edited,
    Rejected,
    Expired,
    Cancelled
}

/// <summary>
/// Persona 状态
/// </summary>
public enum PersonaStatus
{
    Active,
    Paused,
    Disabled
}

/// <summary>
/// 计划状态
/// </summary>
public enum PlanStatus
{
    Draft,
    Active,
    Completed,
    Cancelled
}

/// <summary>
/// 决策类型
/// </summary>
public enum DecisionType
{
    AddTasks,
    Rebalance,
    CancelPlan,
    AdjustPriority
}
