namespace ClawPilot.Core.Models;

/// <summary>
/// 操作结果 — 替代 Python 的 OperationResult
/// </summary>
public class OperationResult
{
    public bool Success { get; init; }
    public int? TaskId { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public int? Count { get; init; }
    public object? Data { get; init; }

    public T? GetData<T>() => Data is T t ? t : default;

    public static OperationResult Ok(string? message = null, int? taskId = null, int? count = null, object? data = null)
        => new() { Success = true, Message = message, TaskId = taskId, Count = count, Data = data };

    public static OperationResult Fail(string error)
        => new() { Success = false, Error = error };
}

/// <summary>
/// 泛型操作结果
/// </summary>
public class OperationResult<T> : OperationResult
{
    public new T? Data { get; init; }

    public static OperationResult<T> Ok(T? data = default, string? message = null, int? taskId = null, int? count = null)
        => new() { Success = true, Message = message, TaskId = taskId, Count = count, Data = data };

    public new static OperationResult<T> Fail(string error)
        => new() { Success = false, Error = error };
}

/// <summary>
/// 任务统计
/// </summary>
public class TaskStatistics
{
    public int Total { get; set; }
    public Dictionary<TaskStatus, int> Status { get; set; } = new();
    public Dictionary<TaskType, int> Type { get; set; } = new();
}