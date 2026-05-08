using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

public class ExecutorRegistry
{
    private readonly Dictionary<TaskType, IExecutor> _executors = new();
    private readonly ILogger? _logger;

    public ExecutorRegistry(
        OpenClawExecutor openClaw,
        HermesExecutor hermes,
        KimiCodeExecutor kimiCode,
        CodeBuddyExecutor codeBuddy,
        AiderExecutor aider,
        CodexExecutor codex,
        QwenCodeExecutor qwenCode,
        ILogger? logger = null)
    {
        _logger = logger;
        Register(openClaw);
        Register(hermes);
        Register(kimiCode);
        Register(codeBuddy);
        Register(aider);
        Register(codex);
        Register(qwenCode);
    }

    public void Register(IExecutor executor)
    {
        _executors[executor.SupportedTaskType] = executor;
        _logger?.LogDebug("注册执行器: {Name} → {TaskType}", executor.Name, executor.SupportedTaskType);
    }

    public IExecutor? GetExecutor(TaskType taskType)
    {
        return _executors.TryGetValue(taskType, out var executor) ? executor : null;
    }

    public IReadOnlyDictionary<TaskType, IExecutor> GetAll() => _executors;

    public List<string> GetRegisteredNames() => _executors.Values.Select(e => e.Name).ToList();

    public async Task<List<ExecutorHealthCheckResult>> CheckAllHealthAsync(CancellationToken ct = default)
    {
        var results = new List<ExecutorHealthCheckResult>();
        foreach (var executor in _executors.Values)
        {
            try
            {
                var result = await executor.HealthCheckAsync(ct);
                results.Add(result);
                _logger?.LogDebug("执行器 {Name} 健康检查: {IsHealthy} - {Message}",
                    result.ExecutorName, result.IsHealthy, result.Message);
            }
            catch (Exception ex)
            {
                results.Add(new ExecutorHealthCheckResult
                {
                    IsHealthy = false,
                    ExecutorName = executor.Name,
                    TaskType = executor.SupportedTaskType,
                    Message = $"健康检查异常: {ex.Message}"
                });
                _logger?.LogError(ex, "执行器 {Name} 健康检查异常", executor.Name);
            }
        }
        return results;
    }

    public async Task<ExecutorHealthCheckResult?> CheckHealthAsync(TaskType taskType, CancellationToken ct = default)
    {
        var executor = GetExecutor(taskType);
        if (executor == null) return null;

        try
        {
            return await executor.HealthCheckAsync(ct);
        }
        catch (Exception ex)
        {
            return new ExecutorHealthCheckResult
            {
                IsHealthy = false,
                ExecutorName = executor.Name,
                TaskType = executor.SupportedTaskType,
                Message = $"健康检查异常: {ex.Message}"
            };
        }
    }
}
