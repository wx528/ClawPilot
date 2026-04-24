using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

using TaskStatus = ClawPilot.Core.Models.TaskStatus;

namespace ClawPilot.Core.Services;

/// <summary>
/// 编排服务 — 替代 Python 的 TaskQueue + YAML 调度
/// 支持：CRON, 循环, 随机化, 最大运行时间
/// </summary>
public class OrchestrationService
{
    private readonly TaskQueueService _taskQueue;
    private readonly ILogger? _logger;
    private readonly RuleDecisionEngine _ruleDecisionEngine;

    private List<ScheduleSpec> _scheduleSpecs;
    private YamlConfig _yamlConfig = new();

    // LLM 相关
    private ILlmClient? _llmClient;
    private LlmDecisionEngine? _llmDecisionEngine;
    private OrchestratorProfile? _activeProfile;
    private string _decisionMode = "rules_only";

    // 任务状态跟踪
    private readonly Dictionary<string, DateTime> _lastRunTimestamps = new();
    private readonly Dictionary<int, string> _taskRunTimes = new();
    private readonly object _taskRunLock = new();

    // 调度任务
    private Timer? _orchestratorTimer;
    private CancellationTokenSource? _cts;

    // 执行状态
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    // 上次调度轮的时间（用于 UI 显示）
    public DateTime? LastScheduleCheckAt { get; private set; }

    // 状态输出
    public string StatusOutput { get; private set; } = "等待配置...";

    public OrchestrationService(TaskQueueService taskQueue, ILogger? logger = null)
    {
        _taskQueue = taskQueue;
        _logger = logger;
        _scheduleSpecs = new List<ScheduleSpec>();
        _ruleDecisionEngine = new RuleDecisionEngine(logger);
    }

    // ==================== 配置 ====================

    /// <summary>
    /// 加载 YAML 配置并验证语法
    /// </summary>
    public async Task<OperationResult> LoadAndParseYamlAsync(string yamlContent)
    {
        try
        {
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                .WithTagMapping("!Schedule", typeof(ScheduleSpec))
                .WithTagMapping("!Interval", typeof(ScheduleSpec))
                .WithTagMapping("!Cron", typeof(ScheduleSpec))
                .Build();

            _yamlConfig = await Task.Run(() => deserializer.Deserialize<YamlConfig>(yamlContent));
            var parsedSpecs = ParseScheduleSpecs();

            if (!parsedSpecs.Any())
                return OperationResult.Fail("解析后未找到有效的调度任务配置");

            _scheduleSpecs = parsedSpecs;
            StatusOutput = $"成功解析 {_scheduleSpecs.Count} 个任务规范，等待启动";
            _logger?.LogInformation("YAML 配置解析成功，任务数: {Count}", _scheduleSpecs.Count);

            return OperationResult.Ok(data: _scheduleSpecs.Count);
        }
        catch (Exception ex)
        {
            var error = $"YAML 配置解析失败: {ex.Message}";
            StatusOutput = error;
            _logger?.LogError(ex, error);
            return OperationResult.Fail(error);
        }
    }

    private List<ScheduleSpec> ParseScheduleSpecs()
    {
        var specs = new List<ScheduleSpec>();

        // 调度任务
        if (_yamlConfig.ScheduleTasks != null)
        {
            foreach (var (taskName, yamlSpec) in _yamlConfig.ScheduleTasks)
            {
                specs.AddRange(ParseScheduleYaml(taskName, yamlSpec));
            }
        }

        // 循环任务
        if (_yamlConfig.LoopTasks != null)
        {
            foreach (var (taskName, yamlSpec) in _yamlConfig.LoopTasks)
            {
                specs.AddRange(ParseLoopYaml(taskName, yamlSpec));
            }
        }

        return specs;
    }

    private List<ScheduleSpec> ParseScheduleYaml(string taskName, object config)
    {
        var list = new List<ScheduleSpec>();

        // 支持单配置或列表
        if (config is System.Collections.Generic.List<object> configList)
        {
            foreach (var item in configList)
            {
                if (item is Dictionary<object, object> itemDict)
                {
                    list.AddRange(ParseScheduleEntry(taskName, itemDict));
                }
            }
        }
        else if (config is Dictionary<object, object> singleDict)
        {
            list.AddRange(ParseScheduleEntry(taskName, singleDict));
        }
        else
        {
            _logger?.LogWarning("任务 {TaskName} 配置格式无效", taskName);
        }

        return list;
    }

    private List<ScheduleSpec> ParseScheduleEntry(string taskName, Dictionary<object, object> singleSpec)
    {
        // 1. 确定触发机制
        var triggerType = "schedule";
        if (singleSpec.TryGetValue("trigger", out var trigVal))
            triggerType = trigVal?.ToString()?.ToLower() ?? "schedule";

        // 2. 解析任务
        var tasks = singleSpec.TryGetValue("tasks", out var tasksVal)
            ? tasksVal as System.Collections.Generic.List<object> ?? new System.Collections.Generic.List<object>()
            : new System.Collections.Generic.List<object>();

        // 3. 解析执行时间选项
        var minExecTime = singleSpec.TryGetValue("min_time", out var minExecVal)
            ? Convert.ToInt32(minExecVal)
            : 0;

        var maxExecTime = singleSpec.TryGetValue("max_time", out var maxExecVal)
            ? Convert.ToInt32(maxExecVal)
            : 3600; // 默认 1h

        var retryCount = singleSpec.TryGetValue("retry", out var retryVal)
            ? Convert.ToInt32(retryVal)
            : 0;

        var randomizedDelay = singleSpec.TryGetValue("randomized", out var randomVal)
            ? Convert.ToBoolean(randomVal)
            : false;

        var randomizedMax = singleSpec.TryGetValue("random_max", out var rmaxVal)
            ? Convert.ToInt32(rmaxVal)
            : 30; // 默认最大加钟 30 分钟

        // 4. 根据类型添加到 specs
        if (triggerType == "cron" && singleSpec.TryGetValue("cron", out var cronVal))
        {
            var cronStr = cronVal?.ToString();
            if (string.IsNullOrEmpty(cronStr))
            {
                _logger?.LogError("任务 {Name} 的 CRON 表达式为空", taskName);
                return new List<ScheduleSpec>();
            }
            _logger?.LogInformation("任务 {Name} 配置为定时调度: {Cron}", taskName, cronStr);

            var nextRunAt = ComputeNextCronRun(cronStr);
            if (nextRunAt == null)
            {
                _logger?.LogError("任务 {Name} 的 CRON 表达式无效: {Cron}", taskName, cronStr);
                return new List<ScheduleSpec>();
            }

            return new List<ScheduleSpec>
            {
                new ScheduleSpec
                {
                    Id = $"{taskName}_cron_{cronStr.GetHashCode()}",
                    TaskName = taskName,
                    Trigger = "cron",
                    Cron = cronStr,
                    NextRun = nextRunAt.Value,
                    Duration = (int)nextRunAt.Value.TimeOfDay.TotalSeconds,
                    Tasks = tasks,
                    MinExecTime = minExecTime,
                    MaxExecTime = maxExecTime,
                    RetryCount = retryCount,
                    RandomizedDelay = randomizedDelay,
                    RandomizedMax = randomizedMax,
                    Enabled = true
                }
            };
        }

        _logger?.LogWarning("任务 {Name} 的触发机制未识别: {Trigger}", taskName, triggerType);
        return new List<ScheduleSpec>();
    }

    private List<ScheduleSpec> ParseLoopYaml(string taskName, object config)
    {
        var list = new List<ScheduleSpec>();
        // 简化版：Loop 只支持 interval
        if (config is Dictionary<object, object> dict)
        {
            if (dict.TryGetValue("interval", out var intervalVal))
            {
                var interval = Convert.ToInt32(intervalVal);

                var spec = new ScheduleSpec
                {
                    Id = $"{taskName}_loop",
                    TaskName = taskName,
                    Trigger = "loop",
                    Duration = interval,
                    NextRun = DateTime.Now, // 第一次立即执行
                    MinExecTime = dict.TryGetValue("min_time", out var m) ? Convert.ToInt32(m) : 0,
                    MaxExecTime = dict.TryGetValue("max_time", out var m2) ? Convert.ToInt32(m2) : 3600,
                    RandomizedDelay = dict.TryGetValue("randomized", out var r) && Convert.ToBoolean(r),
                    RandomizedMax = dict.TryGetValue("random_max", out var r2) ? Convert.ToInt32(r2) : 30,
                    RetryCount = dict.TryGetValue("retry", out var re) ? Convert.ToInt32(re) : 0,
                    Tasks = dict.TryGetValue("tasks", out var t) ? (t as System.Collections.Generic.List<object>) ?? new System.Collections.Generic.List<object>() : new System.Collections.Generic.List<object>(),
                    Enabled = true
                };
                _logger?.LogInformation("任务 {Name} 配置为循环调度: {Interval}秒", taskName, interval);
                list.Add(spec);
            }
        }

        return list;
    }

    // ==================== 调度 ====================

    private async Task TickAsync(CancellationToken ct)
    {
        if (!_isRunning) return;

        LastScheduleCheckAt = DateTime.Now;
        var anyAction = false;

        var ruleResult = _ruleDecisionEngine.Evaluate(_scheduleSpecs, LastScheduleCheckAt.Value);

        foreach (var spec in ruleResult.TriggeredSpecs)
        {
            var result = await ProcessScheduleEntryAsync(spec);
            if (result.Success)
            {
                anyAction = true;
                if (spec.Trigger == "cron")
                    spec.NextRun = ComputeNextCronRun(spec.Cron!).GetValueOrDefault(DateTime.Now.AddDays(1));
                else if (spec.Trigger == "loop")
                    spec.NextRun = DateTime.Now.AddSeconds(spec.Duration);
            }
        }

        if (anyAction)
        {
            StatusOutput = $"调度检查: {LastScheduleCheckAt:HH:mm:ss} - 发现待执行任务";
            _logger?.LogInformation("调度检查完成，发现待执行任务");
        }
        else
        {
            StatusOutput = $"调度检查: {LastScheduleCheckAt:HH:mm:ss} - 无任务";
        }
    }

    private async Task<OperationResult> ProcessScheduleEntryAsync(ScheduleSpec spec)
    {
        var taskStartTime = DateTime.Now;

        // 防止重复执行（内存锁定）
        if (_taskRunTimes.ContainsKey(spec.Id.GetHashCode()))
        {
            return OperationResult.Fail("任务正在内存中执行，跳过");
        }
        _taskRunTimes[spec.Id.GetHashCode()] = DateTime.Now.ToString("O");

        try
        {
            if (spec.Trigger == "loop" || spec.Trigger == "cron")
            {
                // 实际调度逻辑
                if (spec.Tasks.Count == 0)
                    return OperationResult.Ok("任务列表为空，跳过");

                // 随机化延迟
                if (spec.RandomizedDelay)
                {
                    var delay = Random.Shared.Next(0, spec.RandomizedMax);
                    StatusOutput = $"任务 '{spec.TaskName}' 应用随机延迟: {delay}秒";
                    _logger?.LogInformation("任务 '{TaskName}' 应用随机延迟 {Delay}秒", spec.TaskName, delay);
                    await Task.Delay(delay * 1000);
                }

                StatusOutput = $"正在调度 '{spec.TaskName}' ...";

                var results = new List<int>();
                foreach (var task in spec.Tasks)
                {
                    var taskDict = task as Dictionary<object, object>;
                    if (taskDict == null)
                        continue;

                    if (!taskDict.TryGetValue("agent_name", out var rawAgent) || !taskDict.TryGetValue("message", out var rawMsg))
                    {
                        StatusOutput = "解析任务配置失败: 缺少字段";
                        return OperationResult.Fail("任务定义无效: 缺少 agent_name 或 message");
                    }

                    var agentName = rawAgent.ToString()!;
                    var message = rawMsg.ToString()!;

                    var result = await _taskQueue.AddTaskAsync(
                        message: message,
                        agentName: agentName,
                        taskType: TaskType.OpenClaw,
                        source: TaskSource.Orchestrator
                    );

                    if (result.Success)
                    {
                        results.Add(result.TaskId!.Value);
                        StatusOutput = $"已添加任务: {agentName}";
                        _logger?.LogInformation("任务 '{TaskName}' 添加成功，ID: {Id}", spec.TaskName, result.TaskId);
                    }
                    else
                    {
                        StatusOutput = $"任务 '{spec.TaskName}' 调度失败: {result.Error}";
                        _logger?.LogWarning("任务 '{TaskName}' 添加失败: {Error}", spec.TaskName, result.Error);
                    }
                }

                return OperationResult.Ok($"成功调度 '{spec.TaskName}' ({results.Count} 个任务)", count: results.Count);
            }
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
        finally
        {
            _taskRunTimes.Remove(spec.Id.GetHashCode());
        }
        return OperationResult.Fail("未知触发类型");
    }

    private static DateTime? ComputeNextCronRun(string? cronStr)
    {
        if (string.IsNullOrEmpty(cronStr))
            return null;
        try
        {
            var schedule = NCrontab.CrontabSchedule.Parse(cronStr);
            var now = DateTime.Now;
            var next = schedule.GetNextOccurrence(now);
            return next;
        }
        catch
        {
            return null;
        }
    }

    // ==================== 生命周期 ====================

    public Task StartAsync()
    {
        if (_isRunning) return Task.CompletedTask;

        _isRunning = true;
        _cts = new CancellationTokenSource();

        _orchestratorTimer = new Timer(async _ => await TickAsync(_cts.Token), null,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

        // 启动后立即执行一次调度检查
        _ = Task.Run(async () => await TickAsync(_cts.Token));

        _logger?.LogInformation("调度服务已启动，轮询间隔: 3秒");
        StatusOutput = "调度服务运行中...";
        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();
        _orchestratorTimer?.Dispose();
        _orchestratorTimer = null;
        StatusOutput = "调度服务已停止";
        _logger?.LogInformation("调度服务已停止");
    }

    public void Shutdown()
    {
        Stop();
        _cts?.Dispose();
        _taskRunTimes.Clear();
        StatusOutput = "停止并清理...";
    }

    // ==================== LLM 编排周期 ====================

    /// <summary>
    /// 配置 LLM 客户端
    /// </summary>
    public void ConfigureLlm(ILlmClient llmClient)
    {
        _llmClient = llmClient;
        _llmDecisionEngine = new LlmDecisionEngine(llmClient, _logger);
        _logger?.LogInformation("LLM 客户端已配置");
    }

    /// <summary>
    /// 加载编排器 Profile（用于 LLM 决策）
    /// </summary>
    public void LoadProfile(OrchestratorProfile profile)
    {
        _activeProfile = profile;
        _decisionMode = profile.DefaultDecisionMode?.ToLower() ?? "rules_only";
        _logger?.LogInformation("Profile 已加载: {Name}, 决策模式: {Mode}", profile.Name, _decisionMode);
    }

    /// <summary>
    /// 执行编排周期（支持 rules_only / fallback / llm_only）
    /// </summary>
    public async Task<OrchestrationDraft?> RunOrchestrationCycleAsync(CancellationToken ct = default)
    {
        if (_activeProfile == null)
        {
            _logger?.LogWarning("未加载 Profile，无法执行编排周期");
            return null;
        }

        _logger?.LogInformation("执行编排周期，模式: {Mode}", _decisionMode);
        var context = await BuildContextAsync();
        OrchestrationDecision? decision = null;

        switch (_decisionMode)
        {
            case "llm_only":
                if (_llmDecisionEngine == null)
                {
                    _logger?.LogWarning("LLM 决策引擎未配置");
                    return null;
                }
                decision = await _llmDecisionEngine.DecideAsync(_activeProfile, context, ct);
                break;

            case "fallback":
                var ruleResult = _ruleDecisionEngine.Evaluate(_scheduleSpecs, DateTime.Now);
                if (ruleResult.HasDecisions)
                {
                    decision = ConvertRuleResultToDecision(ruleResult);
                    decision.DecisionModeUsed = "fallback(rules)";
                    _logger?.LogInformation("Fallback 模式: 规则引擎触发，跳过 LLM");
                }
                else if (_llmDecisionEngine != null)
                {
                    decision = await _llmDecisionEngine.DecideAsync(_activeProfile, context, ct);
                    if (decision != null)
                        decision.DecisionModeUsed = "fallback(llm)";
                }
                break;

            case "rules_only":
            default:
                _logger?.LogInformation("Rules-only 模式，编排周期由定时调度处理");
                return null;
        }

        if (decision == null || decision.TasksToAdd.Count == 0)
        {
            _logger?.LogInformation("编排周期未产生任何任务");
            return null;
        }

        var draft = CreateDraftFromDecision(decision, context);
        _logger?.LogInformation("编排草案已创建: {Count} 个任务", draft.Items.Count);

        if (_activeProfile.DraftAutoApprove)
        {
            await ExecuteDraftAsync(draft, ct);
        }

        return draft;
    }

    /// <summary>
    /// 预览编排周期（不执行，只返回草案）
    /// </summary>
    public async Task<OrchestrationDraft?> PreviewOrchestrationCycleAsync(CancellationToken ct = default)
    {
        if (_activeProfile == null) return null;

        var context = await BuildContextAsync();
        OrchestrationDecision? decision = null;

        switch (_decisionMode)
        {
            case "llm_only":
                if (_llmDecisionEngine == null) return null;
                decision = await _llmDecisionEngine.DecideAsync(_activeProfile, context, ct);
                break;

            case "fallback":
                var ruleResult = _ruleDecisionEngine.Evaluate(_scheduleSpecs, DateTime.Now);
                if (ruleResult.HasDecisions)
                {
                    decision = ConvertRuleResultToDecision(ruleResult);
                    decision.DecisionModeUsed = "fallback(rules)";
                }
                else if (_llmDecisionEngine != null)
                {
                    decision = await _llmDecisionEngine.DecideAsync(_activeProfile, context, ct);
                    if (decision != null)
                        decision.DecisionModeUsed = "fallback(llm)";
                }
                break;

            default:
                return null;
        }

        if (decision == null) return null;
        return CreateDraftFromDecision(decision, context);
    }

    private async Task<OrchestrationContext> BuildContextAsync()
    {
        var context = new OrchestrationContext
        {
            Now = DateTime.Now,
            ProfileName = _activeProfile?.Name ?? ""
        };

        var stats = await _taskQueue.GetStatisticsAsync();
        if (stats != null)
        {
            context.TotalPendingTasks = stats.Status.GetValueOrDefault(TaskStatus.Pending, 0);
            context.TotalRunningTasks = stats.Status.GetValueOrDefault(TaskStatus.Running, 0);
            context.TotalTasksToday = stats.Total;
        }

        var recent = await _taskQueue.ListTasksAsync(limit: 20);
        context.RecentTasks = recent.Select(t => new TaskSnapshot
        {
            Id = t.Id,
            AgentName = t.AgentName,
            Message = t.Message,
            Status = t.Status,
            CreatedAt = t.CreatedAt,
            Output = t.Output
        }).ToList();

        var runningTasks = await _taskQueue.ListTasksAsync(status: TaskStatus.Running);
        context.PersonaLoad = runningTasks
            .GroupBy(t => t.AgentName)
            .ToDictionary(g => g.Key, g => g.Count());

        if (_activeProfile?.PersonaPresets != null)
        {
            context.AvailablePersonas = _activeProfile.PersonaPresets.Select(p => new Persona
            {
                Name = p.Name,
                DisplayName = p.DisplayName,
                Description = p.Description,
                SystemPrompt = p.SystemPrompt,
                TaskType = p.TaskType,
                MaxConcurrent = p.MaxConcurrent,
                Status = PersonaStatus.Active,
                Tags = p.Tags
            }).ToList();
        }

        return context;
    }

    private OrchestrationDecision ConvertRuleResultToDecision(RuleDecisionResult ruleResult)
    {
        var tasks = new List<TaskToAdd>();
        foreach (var spec in ruleResult.TriggeredSpecs)
        {
            foreach (var taskObj in spec.Tasks)
            {
                if (taskObj is not Dictionary<object, object> taskDict) continue;
                if (!taskDict.TryGetValue("agent_name", out var rawAgent) || !taskDict.TryGetValue("message", out var rawMsg))
                    continue;

                tasks.Add(new TaskToAdd
                {
                    PersonaName = rawAgent?.ToString() ?? "",
                    Message = rawMsg?.ToString() ?? "",
                    TaskType = taskDict.TryGetValue("task_type", out var tt) ? tt?.ToString() ?? "openclaw" : "openclaw",
                    Priority = TaskPriority.Normal,
                    Reason = $"规则触发: {spec.TaskName} ({spec.Trigger})"
                });
            }
        }

        return new OrchestrationDecision
        {
            DecisionType = DecisionType.AddTasks,
            Reasoning = $"规则引擎触发 {ruleResult.TriggeredSpecs.Count} 个调度规范",
            TasksToAdd = tasks
        };
    }

    private OrchestrationDraft CreateDraftFromDecision(OrchestrationDecision decision, OrchestrationContext context)
    {
        var draft = new OrchestrationDraft
        {
            Items = decision.TasksToAdd.Select((t, i) => new DraftItem
            {
                PersonaName = t.PersonaName,
                Message = t.Message,
                TaskType = t.TaskType,
                Priority = t.Priority,
                SourcePlanName = _activeProfile?.Name ?? "",
                SourceItemIndex = i,
                Reason = t.Reason
            }).ToList(),
            ContextSummary = $"Pending:{context.TotalPendingTasks} Running:{context.TotalRunningTasks} Total:{context.TotalTasksToday}",
            DecisionReason = decision.Reasoning,
            DecisionModeUsed = decision.DecisionModeUsed,
            ProfileName = _activeProfile?.Name ?? "",
            Status = DraftStatus.Pending,
            AutoApprove = _activeProfile?.DraftAutoApprove ?? true,
            AutoApproveAfterSeconds = _activeProfile?.DraftAutoApproveAfterSeconds ?? 300
        };

        return draft;
    }

    private async Task ExecuteDraftAsync(OrchestrationDraft draft, CancellationToken ct)
    {
        draft.Status = DraftStatus.Executing;
        var logs = new List<string>();

        foreach (var item in draft.Items)
        {
            try
            {
                var taskType = item.TaskType.Equals("langgraph", StringComparison.OrdinalIgnoreCase)
                    ? TaskType.LangGraph
                    : TaskType.OpenClaw;

                var result = await _taskQueue.AddTaskAsync(
                    message: item.Message,
                    agentName: item.PersonaName,
                    taskType: taskType,
                    source: TaskSource.Orchestrator
                );

                if (result.Success)
                {
                    logs.Add($"[{DateTime.Now:HH:mm:ss}] 成功: {item.PersonaName} -> {Truncate(item.Message, 50)} (ID:{result.TaskId})");
                }
                else
                {
                    logs.Add($"[{DateTime.Now:HH:mm:ss}] 失败: {item.PersonaName} -> {result.Error}");
                }
            }
            catch (Exception ex)
            {
                logs.Add($"[{DateTime.Now:HH:mm:ss}] 异常: {item.PersonaName} -> {ex.Message}");
            }
        }

        draft.ExecutionLog = string.Join("\n", logs);
        draft.Status = DraftStatus.Executed;
        draft.ExecutedAt = DateTime.Now.ToString("O");
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    // ==================== 查询 ====================

    /// <summary>
    /// 获取当前所有调度规范（用于 UI 显示）
    /// </summary>
    public List<ScheduleSpec> GetScheduleSpecs()
    {
        return _scheduleSpecs;
    }

    /// <summary>
    /// 获取任务执行状态
    /// </summary>
    public Dictionary<int, string> GetActiveTasks()
    {
        lock (_taskRunLock)
        {
            return new Dictionary<int, string>(_taskRunTimes);
        }
    }

    /// <summary>
    /// 获取最后运行时间
    /// </summary>
    public DateTime? GetLastRunTime(string taskName)
    {
        if (_lastRunTimestamps.TryGetValue(taskName, out var lastRunTime))
            return lastRunTime;
        return null;
    }
}