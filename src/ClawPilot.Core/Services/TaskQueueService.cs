using ClawPilot.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using TaskStatus = ClawPilot.Core.Models.TaskStatus;
using TaskType = ClawPilot.Core.Models.TaskType;
using TaskSource = ClawPilot.Core.Models.TaskSource;

namespace ClawPilot.Core.Services;

/// <summary>
/// 任务队列服务 — 替代 Python TaskManager + HTTP API 层
/// 直接操作 SQLite，无网络通信
/// </summary>
public class TaskQueueService
{
    private readonly string _dbPath;
    private readonly ILogger? _logger;

    public TaskQueueService(string dbPath, ILogger? logger = null)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    private SqliteConnection GetConnection() => new($"Data Source={_dbPath}");

    // ==================== 初始化 ====================

    /// <summary>
    /// 确保任务表存在且结构完整
    /// </summary>
    public async Task<bool> EnsureTableExistsAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='tasks'";
            var exists = await cmd.ExecuteScalarAsync() != null;

            if (!exists)
            {
                cmd.CommandText = """
                    CREATE TABLE tasks (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        agent_name TEXT NOT NULL,
                        message TEXT NOT NULL,
                        status TEXT DEFAULT 'pending',
                        output TEXT,
                        created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                        updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                        task_type TEXT DEFAULT 'openclaw',
                        source TEXT DEFAULT 'user'
                    )
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            else
            {
                // 自动补全缺失的列
                await EnsureColumnAsync(conn, "task_type", "TEXT DEFAULT 'openclaw'");
                await EnsureColumnAsync(conn, "source", "TEXT DEFAULT 'user'");
                await EnsureColumnAsync(conn, "retry_count", "INTEGER DEFAULT 0");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "确保任务表存在时出错");
            return false;
        }
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string columnName, string columnDef)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(tasks)";
        var columns = new HashSet<string>();
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains(columnName))
        {
            cmd.CommandText = $"ALTER TABLE tasks ADD COLUMN {columnName} {columnDef}";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ==================== 添加任务 ====================

    public async Task<OperationResult> AddTaskAsync(string message, string agentName = "main",
        TaskType taskType = TaskType.OpenClaw, TaskSource source = TaskSource.User)
    {
        _logger?.LogDebug("添加任务，代理: {AgentName}，消息: {Message}，类型: {TaskType}，来源: {Source}", 
            agentName, message, taskType, source);
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var transaction = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) as SqliteTransaction;
        if (transaction == null)
        {
            _logger?.LogError("无法创建数据库事务");
            return OperationResult.Fail("无法创建数据库事务");
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO tasks (agent_name, message, status, task_type, source)
                VALUES (@agentName, @message, 'pending', @taskType, @source);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@agentName", agentName);
            cmd.Parameters.AddWithValue("@message", message);
            cmd.Parameters.AddWithValue("@taskType", taskType.ToString().ToLower());
            cmd.Parameters.AddWithValue("@source", source.ToString().ToLower());

            var taskId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            await transaction.CommitAsync();

            return OperationResult.Ok("任务添加成功", taskId: taskId);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger?.LogError(ex, "添加任务失败");
            return OperationResult.Fail(ex.Message);
        }
    }

    // ==================== 查询任务 ====================

    public async Task<OperationResult> GetTaskAsync(int taskId)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM tasks WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", taskId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return OperationResult.Ok(data: await FormatTaskAsync(reader));
            }
            return OperationResult.Fail("任务不存在");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 获取下一个 pending 任务并原子性地锁定为 running
    /// </summary>
    public async Task<TaskItem?> GetNextPendingAsync()
    {
        _logger?.LogDebug("获取下一个待处理任务");
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var transaction = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) as SqliteTransaction;
        if (transaction == null)
        {
            _logger?.LogError("无法创建数据库事务");
            return null;
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT * FROM tasks WHERE status = 'pending' ORDER BY id LIMIT 1";

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                await transaction.RollbackAsync();
                return null;
            }

            var task = await FormatTaskAsync(reader);
            reader.Close();

            using var updateCmd = conn.CreateCommand();
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = "UPDATE tasks SET status = 'running', updated_at = CURRENT_TIMESTAMP WHERE id = @id";
            updateCmd.Parameters.AddWithValue("@id", task.Id);
            await updateCmd.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
            task.Status = TaskStatus.Running;
            return task;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "获取下一个待处理任务失败");
            await transaction.RollbackAsync();
            return null;
        }
    }

    /// <summary>
    /// 列出任务，支持按条件筛选
    /// </summary>
    public async Task<List<TaskItem>> ListTasksAsync(TaskStatus? status = null, TaskType? taskType = null,
        string? agentName = null, int? limit = null, string? timeRange = null, TaskSource? source = null)
    {
        var results = new List<TaskItem>();
        using var conn = GetConnection();
        await conn.OpenAsync();

        try
        {
            var query = "SELECT * FROM tasks";
            var conditions = new List<string>();
            var parameters = new List<KeyValuePair<string, object?>>();

            if (status.HasValue)
            {
                conditions.Add("status = @status");
                parameters.Add(new("@status", status.Value.ToString().ToLower()));
            }
            if (taskType.HasValue)
            {
                conditions.Add("task_type = @taskType");
                parameters.Add(new("@taskType", taskType.Value.ToString().ToLower()));
            }
            if (!string.IsNullOrEmpty(agentName))
            {
                conditions.Add("agent_name = @agentName");
                parameters.Add(new("@agentName", agentName));
            }
            if (!string.IsNullOrEmpty(timeRange))
            {
                conditions.Add("created_at >= datetime('now', @timeRange)");
                parameters.Add(new("@timeRange", timeRange));
            }
            if (source.HasValue)
            {
                conditions.Add("source = @source");
                parameters.Add(new("@source", source.Value.ToString().ToLower()));
            }

            if (conditions.Count > 0)
                query += " WHERE " + string.Join(" AND ", conditions);

            query += " ORDER BY id DESC";

            if (limit.HasValue)
            {
                query += " LIMIT @limit";
                parameters.Add(new("@limit", limit.Value));
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = query;
            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
            }

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(await FormatTaskAsync(reader));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "列出任务失败");
        }

        return results;
    }

    /// <summary>
    /// 获取最近指定小时内的任务
    /// </summary>
    public async Task<List<TaskItem>> GetRecentTasksAsync(int hours = 1)
    {
        return await ListTasksAsync(timeRange: $"-{hours} hours");
    }

    // ==================== 统计 ====================

    public async Task<TaskStatistics?> GetStatisticsAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        try
        {
            var stats = new TaskStatistics();

            // 按状态统计
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT status, COUNT(*) FROM tasks GROUP BY status ORDER BY status";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var statusStr = reader.GetString(0);
                    if (Enum.TryParse<TaskStatus>(statusStr, true, out var status))
                    {
                        stats.Status[status] = reader.GetInt32(1);
                    }
                }
            }

            // 按类型统计
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT task_type, COUNT(*) FROM tasks GROUP BY task_type ORDER BY task_type";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var typeStr = reader.GetString(0);
                    if (Enum.TryParse<TaskType>(typeStr, true, out var type))
                    {
                        stats.Type[type] = reader.GetInt32(1);
                    }
                }
            }

            // 总数
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM tasks";
                stats.Total = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }

            return stats;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "获取统计信息失败");
            return null;
        }
    }

    // ==================== 更新任务 ====================

    public async Task<OperationResult> UpdateTaskAsync(int taskId, string? agentName = null, string? message = null,
        TaskStatus? status = null, TaskType? taskType = null, string? output = null, TaskSource? source = null)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        try
        {
            // 检查任务是否存在
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT id FROM tasks WHERE id = @id";
            checkCmd.Parameters.AddWithValue("@id", taskId);
            if (await checkCmd.ExecuteScalarAsync() == null)
                return OperationResult.Fail("任务不存在");

            var updates = new List<string>();
            var parameters = new List<KeyValuePair<string, object?>>();

            if (agentName != null) { updates.Add("agent_name = @agentName"); parameters.Add(new("@agentName", agentName)); }
            if (message != null) { updates.Add("message = @message"); parameters.Add(new("@message", message)); }
            if (status.HasValue) { updates.Add("status = @status"); parameters.Add(new("@status", status.Value.ToString().ToLower())); }
            if (taskType.HasValue) { updates.Add("task_type = @taskType"); parameters.Add(new("@taskType", taskType.Value.ToString().ToLower())); }
            if (output != null) { updates.Add("output = @output"); parameters.Add(new("@output", output)); }
            if (source.HasValue) { updates.Add("source = @source"); parameters.Add(new("@source", source.Value.ToString().ToLower())); }

            if (updates.Count == 0)
                return OperationResult.Fail("没有提供需要更新的字段");

            updates.Add("updated_at = CURRENT_TIMESTAMP");

            var query = $"UPDATE tasks SET {string.Join(", ", updates)} WHERE id = @id";
            parameters.Add(new("@id", taskId));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = query;
            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
            }
            await cmd.ExecuteNonQueryAsync();

            return OperationResult.Ok("任务更新成功");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 报告任务执行结果
    /// </summary>
    public async Task<bool> ReportResultAsync(int taskId, TaskStatus status, string output)
    {
        var result = await UpdateTaskAsync(taskId, status: status, output: output);
        return result.Success;
    }

    // ==================== 删除任务 ====================

    public async Task<OperationResult> DeleteTaskAsync(int taskId)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT id FROM tasks WHERE id = @id";
            checkCmd.Parameters.AddWithValue("@id", taskId);
            if (await checkCmd.ExecuteScalarAsync() == null)
                return OperationResult.Fail("任务不存在");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tasks WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", taskId);
            await cmd.ExecuteNonQueryAsync();

            return OperationResult.Ok("任务删除成功");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<int> DeleteTasksAsync(TaskStatus? status = null, TaskType? taskType = null, string? agentName = null)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        try
        {
            var query = "DELETE FROM tasks";
            var conditions = new List<string>();
            var parameters = new List<KeyValuePair<string, object?>>();

            if (status.HasValue) { conditions.Add("status = @status"); parameters.Add(new("@status", status.Value.ToString().ToLower())); }
            if (taskType.HasValue) { conditions.Add("task_type = @taskType"); parameters.Add(new("@taskType", taskType.Value.ToString().ToLower())); }
            if (agentName != null) { conditions.Add("agent_name = @agentName"); parameters.Add(new("@agentName", agentName)); }

            if (conditions.Count > 0)
                query += " WHERE " + string.Join(" AND ", conditions);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = query;
            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
            }

            return await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "删除任务失败");
            return 0;
        }
    }

    public async Task<int> ClearCompletedTasksAsync()
    {
        var successCount = await DeleteTasksAsync(status: TaskStatus.Success);
        var failedCount = await DeleteTasksAsync(status: TaskStatus.Failed);
        return successCount + failedCount;
    }

    public async Task<int> ClearAllTasksAsync()
    {
        var pendingCount = await DeleteTasksAsync(status: TaskStatus.Pending);
        var runningCount = await DeleteTasksAsync(status: TaskStatus.Running);
        var successCount = await DeleteTasksAsync(status: TaskStatus.Success);
        var failedCount = await DeleteTasksAsync(status: TaskStatus.Failed);
        return pendingCount + runningCount + successCount + failedCount;
    }

    /// <summary>
    /// 安排任务重试：将状态重置为 pending 并递增 retry_count
    /// </summary>
    public async Task<bool> ScheduleRetryAsync(int taskId, string output)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET status = 'pending', retry_count = retry_count + 1, output = @output, updated_at = CURRENT_TIMESTAMP WHERE id = @id";
        cmd.Parameters.AddWithValue("@output", output);
        cmd.Parameters.AddWithValue("@id", taskId);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    // ==================== 辅助方法 ====================

    private static Task<TaskItem> FormatTaskAsync(SqliteDataReader reader)
    {
        var task = new TaskItem
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            AgentName = reader.GetString(reader.GetOrdinal("agent_name")),
            Message = reader.GetString(reader.GetOrdinal("message")),
            Output = reader.IsDBNull(reader.GetOrdinal("output")) ? "" : reader.GetString(reader.GetOrdinal("output")),
        };

        // 状态转换
        if (!reader.IsDBNull(reader.GetOrdinal("status")))
        {
            var statusStr = reader.GetString(reader.GetOrdinal("status"));
            if (Enum.TryParse<TaskStatus>(statusStr, true, out var status))
            {
                task.Status = status;
            }
        }

        // 日期时间转换
        if (!reader.IsDBNull(reader.GetOrdinal("created_at")))
        {
            var createdAtStr = reader.GetString(reader.GetOrdinal("created_at"));
            if (DateTime.TryParse(createdAtStr, out var createdAt))
            {
                task.CreatedAt = createdAt;
            }
        }

        if (!reader.IsDBNull(reader.GetOrdinal("updated_at")))
        {
            var updatedAtStr = reader.GetString(reader.GetOrdinal("updated_at"));
            if (DateTime.TryParse(updatedAtStr, out var updatedAt))
            {
                task.UpdatedAt = updatedAt;
            }
        }

        // 任务类型转换
        if (reader.GetOrdinal("task_type") >= 0 && !reader.IsDBNull(reader.GetOrdinal("task_type")))
        {
            var taskTypeStr = reader.GetString(reader.GetOrdinal("task_type"));
            if (Enum.TryParse<TaskType>(taskTypeStr, true, out var taskType))
            {
                task.TaskType = taskType;
            }
        }

        // 任务来源转换
        if (reader.GetOrdinal("source") >= 0 && !reader.IsDBNull(reader.GetOrdinal("source")))
        {
            var sourceStr = reader.GetString(reader.GetOrdinal("source"));
            if (Enum.TryParse<TaskSource>(sourceStr, true, out var source))
            {
                task.Source = source;
            }
        }

        return Task.FromResult(task);
    }
}