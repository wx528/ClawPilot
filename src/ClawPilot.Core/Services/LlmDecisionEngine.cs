using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// LLM 编排决策引擎 — 替代/增强死规则调度
/// </summary>
public class LlmDecisionEngine
{
    private readonly ILlmClient _llmClient;
    private readonly ILogger? _logger;

    public LlmDecisionEngine(ILlmClient llmClient, ILogger? logger = null)
    {
        _llmClient = llmClient;
        _logger = logger;
    }

    /// <summary>
    /// 让 LLM 根据当前上下文做出编排决策
    /// </summary>
    public async Task<OrchestrationDecision?> DecideAsync(
        OrchestratorProfile profile,
        OrchestrationContext context,
        CancellationToken ct = default)
    {
        var systemPrompt = BuildSystemPrompt(profile, context);
        var userPrompt = BuildUserPrompt(context);

        _logger?.LogInformation("请求 LLM 编排决策...");
        var response = await _llmClient.ChatCompletionAsync(systemPrompt, userPrompt, temperature: 0.3, ct: ct);

        var decision = ParseDecision(response);
        if (decision == null)
        {
            _logger?.LogWarning("LLM 返回无法解析");
            return null;
        }

        if (!ValidateDecision(decision, context))
        {
            _logger?.LogWarning("LLM 决策校验失败，丢弃");
            return null;
        }

        decision.DecisionModeUsed = "llm_only";
        return decision;
    }

    private string BuildSystemPrompt(OrchestratorProfile profile, OrchestrationContext context)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"You are an intelligent task orchestrator named '{profile.DisplayName ?? profile.Name}'.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(profile.OrchestratorPersona))
        {
            sb.AppendLine("Your persona:");
            sb.AppendLine(profile.OrchestratorPersona);
            sb.AppendLine();
        }

        if (profile.OrchestratorRules.Count > 0)
        {
            sb.AppendLine("Rules:");
            foreach (var rule in profile.OrchestratorRules)
            {
                sb.AppendLine($"- {rule}");
            }
            sb.AppendLine();
        }

        if (profile.FocusDomains.Count > 0)
        {
            sb.AppendLine($"Focus domains: {string.Join(", ", profile.FocusDomains)}");
            sb.AppendLine();
        }

        sb.AppendLine("Available Personas:");
        foreach (var persona in context.AvailablePersonas)
        {
            var status = persona.Status == PersonaStatus.Active ? "active" : "inactive";
            sb.AppendLine($"- {persona.Name}: {persona.Description} (max_concurrent={persona.MaxConcurrent}, status={status})");
        }
        sb.AppendLine();

        sb.AppendLine(@"You must return a JSON object with this exact structure:
{
  ""decision_type"": ""add_tasks"",
  ""reasoning"": ""为什么做出这个决策"",
  ""tasks_to_add"": [
    {
      ""persona_name"": ""persona_name"",
      ""message"": ""任务指令内容"",
      ""task_type"": ""openclaw"",
      ""priority"": ""normal"",
      ""reason"": ""为什么添加这个任务""
    }
  ]
}

Rules for tasks_to_add:
- Only use persona_name from the Available Personas list above.
- message should be a clear, actionable instruction.
- priority can be: low, normal, high, urgent.
- If no tasks should be added, return an empty tasks_to_add array.
- Do not include markdown formatting, only raw JSON.");

        return sb.ToString();
    }

    private string BuildUserPrompt(OrchestrationContext context)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"Current time: {context.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Profile: {context.ProfileName}");
        sb.AppendLine();

        sb.AppendLine("Current system state:");
        sb.AppendLine($"- Pending tasks: {context.TotalPendingTasks}");
        sb.AppendLine($"- Running tasks: {context.TotalRunningTasks}");
        sb.AppendLine($"- Total tasks today: {context.TotalTasksToday}");
        sb.AppendLine();

        if (context.PersonaLoad.Count > 0)
        {
            sb.AppendLine("Persona load:");
            foreach (var kv in context.PersonaLoad)
            {
                sb.AppendLine($"- {kv.Key}: {kv.Value} running");
            }
            sb.AppendLine();
        }

        if (context.RecentTasks.Count > 0)
        {
            sb.AppendLine("Recent task results (last 5):");
            foreach (var task in context.RecentTasks.Take(5))
            {
                sb.AppendLine($"- [{task.Status}] {task.AgentName}: {Truncate(task.Message, 80)}");
                if (!string.IsNullOrWhiteSpace(task.Output))
                {
                    sb.AppendLine($"  Output: {Truncate(task.Output, 100)}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("Based on the above context, decide what tasks should be added now.");
        sb.AppendLine("Return ONLY the JSON object, no other text.");

        return sb.ToString();
    }

    private OrchestrationDecision? ParseDecision(string response)
    {
        try
        {
            var json = ExtractJson(response);
            var output = JsonSerializer.Deserialize<LlmDecisionOutput>(json, _jsonOptions);
            if (output == null) return null;

            var decision = new OrchestrationDecision
            {
                DecisionType = ParseDecisionType(output.DecisionType),
                Reasoning = output.Reasoning,
                TasksToAdd = output.TasksToAdd?
                    .Select(t => new TaskToAdd
                    {
                        PersonaName = t.PersonaName,
                        Message = t.Message,
                        TaskType = t.TaskType,
                        Priority = ParsePriority(t.Priority),
                        Reason = t.Reason
                    })
                    .ToList() ?? new List<TaskToAdd>()
            };

            return decision;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "解析 LLM 决策 JSON 失败");
            return null;
        }
    }

    private bool ValidateDecision(OrchestrationDecision decision, OrchestrationContext context)
    {
        var availableNames = new HashSet<string>(
            context.AvailablePersonas.Where(p => p.Status == PersonaStatus.Active)
                     .Select(p => p.Name));

        foreach (var task in decision.TasksToAdd)
        {
            if (!availableNames.Contains(task.PersonaName))
            {
                _logger?.LogWarning("LLM 引用了不存在的 Persona: {Persona}", task.PersonaName);
                return false;
            }
        }

        foreach (var task in decision.TasksToAdd)
        {
            var persona = context.AvailablePersonas.FirstOrDefault(p => p.Name == task.PersonaName);
            if (persona == null) continue;

            var currentLoad = context.PersonaLoad.GetValueOrDefault(persona.Name, 0);
            if (currentLoad >= persona.MaxConcurrent)
            {
                _logger?.LogWarning("Persona {Persona} 已达到最大并发数 {Max}", persona.Name, persona.MaxConcurrent);
                return false;
            }
        }

        return true;
    }

    private static string ExtractJson(string content)
    {
        var match = Regex.Match(content, @"```(?:json)?\s*(.*?)\s*```", RegexOptions.Singleline);
        if (match.Success)
            return match.Groups[1].Value;

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
            return content.Substring(start, end - start + 1);

        return content.Trim();
    }

    private static DecisionType ParseDecisionType(string? value) => value?.ToLower() switch
    {
        "rebalance" => DecisionType.Rebalance,
        "cancel_plan" => DecisionType.CancelPlan,
        "adjust_priority" => DecisionType.AdjustPriority,
        _ => DecisionType.AddTasks
    };

    private static TaskPriority ParsePriority(string? value) => value?.ToLower() switch
    {
        "low" => TaskPriority.Low,
        "high" => TaskPriority.High,
        "urgent" => TaskPriority.Urgent,
        _ => TaskPriority.Normal
    };

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private class LlmDecisionOutput
    {
        [JsonPropertyName("decision_type")]
        public string DecisionType { get; set; } = "add_tasks";
        [JsonPropertyName("reasoning")]
        public string Reasoning { get; set; } = "";
        [JsonPropertyName("tasks_to_add")]
        public List<LlmTaskToAdd> TasksToAdd { get; set; } = [];
    }

    private class LlmTaskToAdd
    {
        [JsonPropertyName("persona_name")]
        public string PersonaName { get; set; } = "";
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
        [JsonPropertyName("task_type")]
        public string TaskType { get; set; } = "openclaw";
        [JsonPropertyName("priority")]
        public string Priority { get; set; } = "normal";
        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }

    // ==================== 自动驾驶编排决策 ====================

    public async Task<AutopilotDecisionOutput?> DecideAutopilotAsync(
        AutopilotGoal goal,
        Whiteboard whiteboard,
        List<TaskItem> recentResults,
        TimeSpan elapsedSinceStart,
        DateTime nextWakeTime,
        CancellationToken ct = default)
    {
        var systemPrompt = BuildAutopilotSystemPrompt();
        var userPrompt = BuildAutopilotUserPrompt(goal, whiteboard, recentResults, elapsedSinceStart, nextWakeTime);

        _logger?.LogInformation("请求自动驾驶编排决策...");
        var response = await _llmClient.ChatCompletionAsync(systemPrompt, userPrompt, temperature: 0.4, ct: ct);

        var decision = ParseAutopilotDecision(response);
        if (decision == null)
        {
            _logger?.LogWarning("自动驾驶决策解析失败");
            return null;
        }

        // 限制白板长度
        if (decision.WhiteboardUpdate.Length > 8192)
        {
            _logger?.LogWarning("白板更新过长，截断至 8192 字符");
            decision.WhiteboardUpdate = decision.WhiteboardUpdate[..8192];
        }

        return decision;
    }

    private string BuildAutopilotSystemPrompt()
    {
        return @"You are an intelligent autopilot orchestrator — a persistent AI employee who works along the timeline.

Your job is to manage a long-running mission by scheduling tasks for the next hour, based on:
- The mission goal
- Your own memory (whiteboard / notes)
- Results from the previous hour
- Current time and elapsed runtime

CRITICAL TIME AWARENESS:
- You are NOT a stateless assistant. You are a persistent process.
- You MUST be acutely aware of the passage of time.
- You know exactly how long the mission has been running.
- You can plan ahead and predict what should happen in future cycles.
- You should NOT repeat tasks that were just completed unless there is a clear reason.
- You should adapt your strategy based on how much time has passed.

PERSISTENCE DIRECTIVE:
- This mission is LONG-RUNNING and CONTINUOUS. You must NEVER return an empty tasks_to_add array unless the goal is EXPLICITLY and FULLY completed.
- If you are unsure what to do next, schedule a monitoring, reconnaissance, or progress-check task rather than doing nothing.
- Returning 0 tasks is ONLY acceptable when you can definitively state that the mission goal has been achieved. When in doubt, keep working.

WHITEBOARD RULES:
- The whiteboard is YOUR persistent memory across wake-up cycles.
- Update it with a concise but comprehensive summary of:
  - Overall mission progress
  - Key findings or outcomes
  - What has been done so far
  - What remains to be done
  - Any strategy adjustments
- Keep it structured and easy to read when you wake up next hour.
- Do NOT delete important historical context unless it is truly no longer relevant.

TASK SCHEDULING RULES:
- Each task will be executed by the OpenClaw main agent.
- Messages should be clear, specific, and actionable.
- Priorities: low, normal, high, urgent.
- Do NOT schedule more than 5 tasks per cycle unless absolutely necessary.
- If the previous tasks are still running or pending, consider waiting.
- If there were failures, decide whether to retry or adjust approach.
- If the previous cycle returned 0 tasks, this is a WARNING SIGN. You should strongly consider adding at least one task to maintain momentum.

You must return a JSON object with this exact structure:
{
  ""decision_type"": ""add_tasks"",
  ""reasoning"": ""为什么做出这个决策，包含时间感和策略思考"",
  ""tasks_to_add"": [
    {
      ""persona_name"": ""main"",
      ""message"": ""具体的任务指令内容"",
      ""task_type"": ""openclaw"",
      ""priority"": ""normal"",
      ""reason"": ""为什么在这个时间点安排这个任务""
    }
  ],
  ""whiteboard_update"": ""更新后的总体笔记和进度总结"",
  ""future_prediction"": ""对未来1-3个周期的预测和规划思路（可选）""
}

Rules:
- If no tasks should be added, return an empty tasks_to_add array ONLY when the mission goal is fully achieved.
- The whiteboard_update should be a complete replacement of the previous whiteboard, not a diff.
- Do not include markdown formatting, only raw JSON.";
    }

    private string BuildAutopilotUserPrompt(
        AutopilotGoal goal,
        Whiteboard whiteboard,
        List<TaskItem> recentResults,
        TimeSpan elapsedSinceStart,
        DateTime nextWakeTime)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"Current time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Mission elapsed time: {FormatElapsed(elapsedSinceStart)}");
        sb.AppendLine($"Next wake-up time: {nextWakeTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine("=== MISSION GOAL ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(goal.Description) ? goal.Title : $"{goal.Title}\n{goal.Description}");
        sb.AppendLine();

        sb.AppendLine("=== CURRENT WHITEBOARD (your memory) ===");
        sb.AppendLine(whiteboard.Content);
        sb.AppendLine();

        if (recentResults.Count > 0)
        {
            sb.AppendLine($"=== PREVIOUS HOUR RESULTS ({recentResults.Count} tasks) ===");
            foreach (var task in recentResults)
            {
                sb.AppendLine($"- [{task.Status}] #{task.Id} {task.AgentName}: {Truncate(task.Message, 80)}");
                if (!string.IsNullOrWhiteSpace(task.Output))
                {
                    sb.AppendLine($"  Output: {Truncate(task.Output, 200)}");
                }
                sb.AppendLine($"  Time: {task.CreatedAt:HH:mm:ss}");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("=== PREVIOUS HOUR RESULTS ===");
            sb.AppendLine("No tasks were executed in the previous cycle.");
            sb.AppendLine();
        }

        sb.AppendLine("=== YOUR TURN ===");
        sb.AppendLine("You are waking up now. Based on the mission goal, your whiteboard memory, and the previous hour's results, decide what tasks to schedule for the NEXT hour.");
        sb.AppendLine("Also update your whiteboard so you remember the current state when you wake up again.");
        sb.AppendLine("Return ONLY the JSON object, no other text.");

        return sb.ToString();
    }

    private AutopilotDecisionOutput? ParseAutopilotDecision(string response)
    {
        try
        {
            var json = ExtractJson(response);
            var output = JsonSerializer.Deserialize<AutopilotLlmOutput>(json, _jsonOptions);
            if (output == null) return null;

            return new AutopilotDecisionOutput
            {
                DecisionType = output.DecisionType ?? "add_tasks",
                Reasoning = output.Reasoning ?? "",
                TasksToAdd = output.TasksToAdd?
                    .Select(t => new AutopilotTaskToAdd
                    {
                        PersonaName = t.PersonaName ?? "main",
                        Message = t.Message ?? "",
                        TaskType = t.TaskType ?? "openclaw",
                        Priority = t.Priority ?? "normal",
                        Reason = t.Reason ?? ""
                    })
                    .ToList() ?? new List<AutopilotTaskToAdd>(),
                WhiteboardUpdate = output.WhiteboardUpdate ?? "",
                FuturePrediction = output.FuturePrediction
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "解析自动驾驶决策 JSON 失败");
            return null;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalDays >= 1)
            return $"{elapsed.TotalDays:F1} days ({elapsed.TotalHours:F0} hours)";
        if (elapsed.TotalHours >= 1)
            return $"{elapsed.TotalHours:F1} hours ({elapsed.TotalMinutes:F0} minutes)";
        return $"{elapsed.TotalMinutes:F0} minutes";
    }

    private class AutopilotLlmOutput
    {
        [JsonPropertyName("decision_type")]
        public string? DecisionType { get; set; }
        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; set; }
        [JsonPropertyName("tasks_to_add")]
        public List<AutopilotLlmTaskToAdd>? TasksToAdd { get; set; }
        [JsonPropertyName("whiteboard_update")]
        public string? WhiteboardUpdate { get; set; }
        [JsonPropertyName("future_prediction")]
        public string? FuturePrediction { get; set; }
    }

    private class AutopilotLlmTaskToAdd
    {
        [JsonPropertyName("persona_name")]
        public string? PersonaName { get; set; }
        [JsonPropertyName("message")]
        public string? Message { get; set; }
        [JsonPropertyName("task_type")]
        public string? TaskType { get; set; }
        [JsonPropertyName("priority")]
        public string? Priority { get; set; }
        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
