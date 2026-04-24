using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// 规则编排决策引擎 — 基于 YAML 配置的 Cron/Loop 触发
/// </summary>
public class RuleDecisionEngine
{
    private readonly ILogger? _logger;

    public RuleDecisionEngine(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 评估当前有哪些 ScheduleSpec 应该触发
    /// </summary>
    public RuleDecisionResult Evaluate(List<ScheduleSpec> specs, DateTime now)
    {
        var triggered = new List<ScheduleSpec>();
        foreach (var spec in specs)
        {
            if (!spec.Enabled)
                continue;
            if (spec.NextRun > now)
                continue;

            triggered.Add(spec);
        }

        if (triggered.Any())
        {
            _logger?.LogInformation("规则引擎触发 {Count} 个任务规范", triggered.Count);
        }

        return new RuleDecisionResult
        {
            TriggeredSpecs = triggered,
            HasDecisions = triggered.Count > 0
        };
    }
}

public class RuleDecisionResult
{
    public List<ScheduleSpec> TriggeredSpecs { get; set; } = [];
    public bool HasDecisions { get; set; }
}
