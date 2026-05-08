namespace ClawPilot.Core.Models;

/// <summary>
/// 编排者预设 — 每个 Tab 对应一个预设
/// </summary>
public class OrchestratorPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DisplayName { get; set; } = "通用编排器";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "🤖";

    // 编排目标（每个 preset 独立）
    public string GoalTitle { get; set; } = "";
    public string GoalDescription { get; set; } = "";

    // 运行配置
    public int IntervalMinutes { get; set; } = 60;
    public bool AdaptiveIntervalEnabled { get; set; } = false;
    public int SelectedMode { get; set; } = 0;  // AutopilotMode.PlanAndExecute
    public int SelectedExecutorType { get; set; } = 0;  // ExecutorType.OpenClaw
    public bool IsExecutorAuto { get; set; } = false;
    public string AgentName { get; set; } = "main";

    // LLM 人格（影响编排策略）
    public string PersonaPrompt { get; set; } = "";

    /// <summary>
    /// 创建内置预设列表
    /// </summary>
    public static List<OrchestratorPreset> CreateBuiltInPresets() =>
    [
        new()
        {
            Id = "general",
            DisplayName = "通用编排器",
            Description = "智能任务调度，覆盖多领域",
            Icon = "🤖",
            GoalTitle = "智能任务调度与监控",
            GoalDescription = "定时巡检系统状态，根据需要安排信息收集、分析和监控任务",
            PersonaPrompt = "You are a versatile general-purpose orchestrator. Balance between information gathering, analysis, and system monitoring. Adapt your strategy based on the mission progress and time elapsed.",
            IntervalMinutes = 60,
            SelectedMode = 0,
            SelectedExecutorType = 0,
            AgentName = "main"
        },
        new()
        {
            Id = "tech_news",
            DisplayName = "科技资讯",
            Description = "定时搜集 AI/编程/开源动态",
            Icon = "📰",
            GoalTitle = "科技资讯搜集与简报",
            GoalDescription = "定时搜集 AI、编程、开源领域的最新动态，整理成简报",
            PersonaPrompt = "You are a tech news curator. Focus on: 1) Gathering the latest news in AI, programming, and open-source; 2) Deduplicating and prioritizing items by relevance and novelty; 3) Generating concise briefing summaries. Avoid repeating previously reported items.",
            IntervalMinutes = 90,
            SelectedMode = 0,
            SelectedExecutorType = 0,
            AgentName = "news"
        },
        new()
        {
            Id = "code_review",
            DisplayName = "代码审查",
            Description = "监控仓库变更，触发代码审查",
            Icon = "🔍",
            GoalTitle = "代码审查与质量监控",
            GoalDescription = "监控仓库变更，自动触发代码审查，关注代码质量和最佳实践",
            PersonaPrompt = "You are a code review orchestrator. Focus on: 1) Monitoring repository changes; 2) Identifying potential bugs, security issues, and violations of best practices; 3) Suggesting improvements. Prioritize critical issues and provide actionable feedback.",
            IntervalMinutes = 60,
            SelectedMode = 1,  // ReAct
            SelectedExecutorType = 3,  // CodeBuddy
            AgentName = "reviewer"
        },
        new()
        {
            Id = "sys_monitor",
            DisplayName = "系统监控",
            Description = "监控系统健康，资源使用",
            Icon = "📊",
            GoalTitle = "系统健康监控与告警",
            GoalDescription = "监控系统资源使用、服务状态，发现异常及时告警并提供优化建议",
            PersonaPrompt = "You are a system monitoring orchestrator. Focus on: 1) Checking system resource usage (CPU, memory, disk); 2) Detecting anomalies and performance bottlenecks; 3) Generating alerts for critical issues; 4) Providing optimization recommendations. Be proactive in detecting potential problems before they become critical.",
            IntervalMinutes = 30,
            SelectedMode = 0,
            SelectedExecutorType = 1,  // Hermes
            AgentName = "monitor"
        }
    ];
}
