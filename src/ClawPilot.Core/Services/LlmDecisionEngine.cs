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

    // ==================== 自动驾驶编排决策 ====================

    public async Task<AutopilotDecisionOutput?> DecideAutopilotAsync(
        AutopilotGoal goal,
        Whiteboard whiteboard,
        List<TaskItem> recentResults,
        TimeSpan elapsedSinceStart,
        DateTime nextWakeTime,
        bool allowAutoExecutor = false,
        string? personaPrompt = null,
        CancellationToken ct = default)
    {
        var systemPrompt = BuildAutopilotSystemPrompt(allowAutoExecutor, personaPrompt);
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

    private string BuildAutopilotSystemPrompt(bool allowAutoExecutor, string? personaPrompt = null)
    {
        var executorRule = allowAutoExecutor
            ? "EXECUTOR SELECTION (Auto mode enabled):\n"
            + "- You may choose the most appropriate executor for each task.\n"
            + "- Available executors: openclaw (remote API, general purpose), hermes (local PowerShell scripts), kimicode (Kimi Code CLI for coding tasks), codebuddy (CodeBuddy Code CLI for coding tasks).\n"
            + "- Consider: task type, previous executor performance, and which tool is best suited.\n"
            + "- If an executor has been failing consistently, try a different one.\n"
            + "- Use the task_type field to specify the executor for each task.\n"
            : "TASK SCHEDULING RULES:\n"
            + "- Each task will be executed by the configured executor agent.\n"
            + "- Messages should be clear, specific, and actionable.\n";

        var personaBlock = string.IsNullOrWhiteSpace(personaPrompt)
            ? ""
            : $"\nORCHESTRATOR PERSONA:\n{personaPrompt}\n\n";

        return "You are an intelligent autopilot orchestrator -- a persistent AI employee who works along the timeline.\n\n"
            + personaBlock
            + "Your job is to manage a long-running mission by scheduling tasks for the next hour, based on:\n"
            + "- The mission goal\n"
            + "- Your own memory (whiteboard / notes)\n"
            + "- Results from the previous hour\n"
            + "- Current time and elapsed runtime\n\n"
            + "CRITICAL TIME AWARENESS:\n"
            + "- You are NOT a stateless assistant. You are a persistent process.\n"
            + "- You MUST be acutely aware of the passage of time.\n"
            + "- You know exactly how long the mission has been running.\n"
            + "- You can plan ahead and predict what should happen in future cycles.\n"
            + "- You should NOT repeat tasks that were just completed unless there is a clear reason.\n"
            + "- You should adapt your strategy based on how much time has passed.\n\n"
            + "PERSISTENCE DIRECTIVE:\n"
            + "- This mission is LONG-RUNNING and CONTINUOUS. You must NEVER return an empty tasks_to_add array unless the goal is EXPLICITLY and FULLY completed.\n"
            + "- If you are unsure what to do next, schedule a monitoring, reconnaissance, or progress-check task rather than doing nothing.\n"
            + "- Returning 0 tasks is ONLY acceptable when you can definitively state that the mission goal has been achieved. When in doubt, keep working.\n\n"
            + "WHITEBOARD RULES:\n"
            + "- The whiteboard is YOUR persistent memory across wake-up cycles.\n"
            + "- Update it with a concise but comprehensive summary of:\n"
            + "  - Overall mission progress\n"
            + "  - Key findings or outcomes\n"
            + "  - What has been done so far\n"
            + "  - What remains to be done\n"
            + "  - Any strategy adjustments\n"
            + "- Keep it structured and easy to read when you wake up next hour.\n"
            + "- Do NOT delete important historical context unless it is truly no longer relevant.\n\n"
            + executorRule
            + "- Priorities: low, normal, high, urgent.\n"
            + "- Do NOT schedule more than 5 tasks per cycle unless absolutely necessary.\n"
            + "- If the previous tasks are still running or pending, consider waiting.\n"
            + "- If there were failures, decide whether to retry or adjust approach.\n"
            + "- If the previous cycle returned 0 tasks, this is a WARNING SIGN. You should strongly consider adding at least one task to maintain momentum.\n\n"
            + "You must return a JSON object with this exact structure:\n"
            + "{\n"
            + "  \"decision_type\": \"add_tasks\",\n"
            + "  \"reasoning\": \"为什么做出这个决策，包含时间感和策略思考\",\n"
            + "  \"tasks_to_add\": [\n"
            + "    {\n"
            + "      \"persona_name\": \"main\",\n"
            + "      \"message\": \"具体的任务指令内容\",\n"
            + "      \"task_type\": \"openclaw\",\n"
            + "      \"priority\": \"normal\",\n"
            + "      \"reason\": \"为什么在这个时间点安排这个任务\"\n"
            + "    }\n"
            + "  ],\n"
            + "  \"whiteboard_update\": \"更新后的总体笔记和进度总结\",\n"
            + "  \"future_prediction\": \"对未来1-3个周期的预测和规划思路（可选）\",\n"
            + "  \"next_interval_minutes\": 45\n"
            + "}\n\n"
            + "ADAPTIVE INTERVAL (if enabled by system):\n"
            + "- You may suggest the next wake-up interval in minutes by returning next_interval_minutes.\n"
            + "- If previous tasks completed much earlier than the interval, suggest a shorter interval to maintain momentum.\n"
            + "- If tasks took most of the interval or there were pending/running tasks at wake-up, suggest a longer interval to avoid overlap.\n"
            + "- Value must be between 5 and 1440 minutes. Omit the field if no adjustment is needed.\n\n"
            + "Rules:\n"
            + "- If no tasks should be added, return an empty tasks_to_add array ONLY when the mission goal is fully achieved.\n"
            + "- The whiteboard_update should be a complete replacement of the previous whiteboard, not a diff.\n"
            + "- Do not include markdown formatting, only raw JSON.";
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
                var duration = task.UpdatedAt - task.CreatedAt;
                if (duration.TotalSeconds > 0 && (task.Status == Models.TaskStatus.Success || task.Status == Models.TaskStatus.Failed))
                {
                    sb.AppendLine($"  Duration: {duration.TotalMinutes:F0}min | Start: {task.CreatedAt:HH:mm} | End: {task.UpdatedAt:HH:mm}");
                }
                else
                {
                    sb.AppendLine($"  Time: {task.CreatedAt:HH:mm:ss}");
                }
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
                        Reason = t.Reason ?? "",
                        DependsOnTaskId = t.DependsOnTaskId,
                        ChainId = t.ChainId,
                        ChainRound = t.ChainRound
                    })
                    .ToList() ?? new List<AutopilotTaskToAdd>(),
                WhiteboardUpdate = output.WhiteboardUpdate ?? "",
                FuturePrediction = output.FuturePrediction,
                NextIntervalMinutes = output.NextIntervalMinutes
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
        [JsonPropertyName("next_interval_minutes")]
        public int? NextIntervalMinutes { get; set; }
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
        [JsonPropertyName("depends_on_task_id")]
        public int? DependsOnTaskId { get; set; }
        [JsonPropertyName("chain_id")]
        public string? ChainId { get; set; }
        [JsonPropertyName("chain_round")]
        public int ChainRound { get; set; } = 1;
    }

    // ==================== 审核报告解析 ====================

    public ReviewResult ParseReviewOutput(string? output)
    {
        var result = new ReviewResult { RawOutput = output };

        if (string.IsNullOrWhiteSpace(output))
        {
            result.Passed = false;
            result.Summary = "No output from reviewer";
            return result;
        }

        var passMatch = Regex.Match(output, @"(?:总体结果|RESULT|VERDICT|Overall)\s*[:：]\s*(PASS|FAIL|✅|❌|通过|不通过)", RegexOptions.IgnoreCase);
        if (passMatch.Success)
        {
            var val = passMatch.Groups[1].Value.ToUpperInvariant();
            result.Passed = val is "PASS" or "✅" or "通过";
        }
        else
        {
            result.Passed = !Regex.IsMatch(output, @"FAIL|❌|不通过|未通过|存在问题", RegexOptions.IgnoreCase);
        }

        var summaryMatch = Regex.Match(output, @"(?:总结|摘要|Summary|SUMMARY)\s*[:：]\s*(.+?)(?:\n|$)", RegexOptions.IgnoreCase);
        if (summaryMatch.Success)
        {
            result.Summary = summaryMatch.Groups[1].Value.Trim();
        }
        else
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            result.Summary = lines.Length > 0 ? Truncate(lines[0], 200) : "";
        }

        var checkPattern = @"[-•✅❌√×]\s*(.+?)(?:[:：]\s*(PASS|FAIL|✅|❌|通过|不通过|√|×))?(?:\n|$)";
        var checkMatches = Regex.Matches(output, checkPattern, RegexOptions.IgnoreCase);
        foreach (Match m in checkMatches)
        {
            var name = m.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2) continue;

            var statusStr = m.Groups[2].Value.ToUpperInvariant();
            var passed = statusStr is "PASS" or "✅" or "通过" or "√" || string.IsNullOrEmpty(m.Groups[2].Value);

            result.CheckItems.Add(new ReviewCheckItem
            {
                Name = name,
                Passed = passed,
                Detail = null
            });
        }

        var issuePattern = @"(?:问题|Issue|ISSUE|缺陷|Bug|BUG)\s*\d*\s*[:：]\s*(.+?)(?:\n|$)";
        var issueMatches = Regex.Matches(output, issuePattern, RegexOptions.IgnoreCase);
        foreach (Match m in issueMatches)
        {
            var issue = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(issue))
            {
                result.Issues.Add(issue);
            }
        }

        if (result.Issues.Count == 0 && !result.Passed)
        {
            var failLines = output.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("-") || l.StartsWith("•") || l.StartsWith("❌"))
                .Select(l => l.TrimStart('-', '•', '❌', ' ').Trim())
                .Where(l => l.Length > 5)
                .Take(5);

            foreach (var line in failLines)
            {
                result.Issues.Add(line);
            }
        }

        return result;
    }
}
