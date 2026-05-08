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
    CodeBuddy,
    Aider,
    Codex,
    QwenCode
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
    Aider,
    Codex,
    QwenCode,
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
