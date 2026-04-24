using ClawPilot.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// orchestrator.db 存储服务 — 管理 goals / whiteboards / sessions 表
/// </summary>
public class OrchestratorStorageService
{
    private readonly string _dbPath;
    private readonly ILogger? _logger;

    public OrchestratorStorageService(string dbPath, ILogger? logger = null)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    private SqliteConnection GetConnection() => new($"Data Source={_dbPath}");

    // ==================== 初始化 ====================

    public async Task EnsureTablesExistAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        try
        {
            using var cmd = conn.CreateCommand();

            // 目标表
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS goals (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT NOT NULL,
                    description TEXT NOT NULL DEFAULT '',
                    is_active INTEGER NOT NULL DEFAULT 1,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )
                """;
            await cmd.ExecuteNonQueryAsync();

            // 白板表
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS whiteboards (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content TEXT NOT NULL DEFAULT '',
                    version INTEGER NOT NULL DEFAULT 1,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )
                """;
            await cmd.ExecuteNonQueryAsync();

            // 会话记录表
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    triggered_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    completed_at TIMESTAMP,
                    decision_summary TEXT NOT NULL DEFAULT '',
                    tasks_scheduled INTEGER NOT NULL DEFAULT 0,
                    tasks_succeeded INTEGER NOT NULL DEFAULT 0,
                    tasks_failed INTEGER NOT NULL DEFAULT 0,
                    whiteboard_before TEXT,
                    whiteboard_after TEXT,
                    raw_decision_json TEXT,
                    status TEXT NOT NULL DEFAULT 'pending',
                    error_message TEXT
                )
                """;
            await cmd.ExecuteNonQueryAsync();

            // 确保至少有一条白板记录
            cmd.CommandText = "SELECT COUNT(*) FROM whiteboards";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                cmd.CommandText = """
                    INSERT INTO whiteboards (content, version)
                    VALUES ('任务刚开始，还没有任何进展和总结。', 1)
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            _logger?.LogInformation("orchestrator.db 表初始化完成");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "orchestrator.db 表初始化失败");
            throw;
        }
    }

    // ==================== Goals ====================

    public async Task<int> CreateGoalAsync(string title, string description = "")
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO goals (title, description, is_active)
            VALUES (@title, @desc, 1);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@desc", description);

        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        _logger?.LogInformation("创建目标 #{Id}: {Title}", id, title);
        return id;
    }

    public async Task<AutopilotGoal?> GetActiveGoalAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM goals WHERE is_active = 1 ORDER BY id DESC LIMIT 1";

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadGoal(reader);
        }
        return null;
    }

    public async Task<List<AutopilotGoal>> ListGoalsAsync()
    {
        var results = new List<AutopilotGoal>();
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM goals ORDER BY id DESC";

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadGoal(reader));
        }
        return results;
    }

    public async Task<bool> UpdateGoalAsync(int id, string? title = null, string? description = null, bool? isActive = null)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        var updates = new List<string>();
        if (title != null) updates.Add("title = @title");
        if (description != null) updates.Add("description = @desc");
        if (isActive.HasValue) updates.Add("is_active = @active");
        if (updates.Count == 0) return false;

        updates.Add("updated_at = CURRENT_TIMESTAMP");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE goals SET {string.Join(", ", updates)} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        if (title != null) cmd.Parameters.AddWithValue("@title", title);
        if (description != null) cmd.Parameters.AddWithValue("@desc", description);
        if (isActive.HasValue) cmd.Parameters.AddWithValue("@active", isActive.Value ? 1 : 0);

        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteGoalAsync(int id)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM goals WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    // ==================== Whiteboard ====================

    public async Task<Whiteboard> GetLatestWhiteboardAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM whiteboards ORDER BY id DESC LIMIT 1";

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadWhiteboard(reader);
        }

        // 兜底：返回空白板
        return new Whiteboard { Content = "", Version = 0 };
    }

    public async Task<Whiteboard> UpdateWhiteboardAsync(string content)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        // 获取当前版本号
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM whiteboards ORDER BY id DESC LIMIT 1";
        var versionObj = await cmd.ExecuteScalarAsync();
        var version = versionObj != null ? Convert.ToInt32(versionObj) : 0;

        // 插入新记录（追加历史）
        cmd.CommandText = """
            INSERT INTO whiteboards (content, version)
            VALUES (@content, @version);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@version", version + 1);

        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        _logger?.LogInformation("白板已更新，版本: {Version}", version + 1);

        return new Whiteboard
        {
            Id = id,
            Content = content,
            Version = version + 1
        };
    }

    // ==================== Sessions ====================

    public async Task<int> BeginSessionAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (triggered_at, status)
            VALUES (CURRENT_TIMESTAMP, 'pending');
            SELECT last_insert_rowid();
            """;

        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        _logger?.LogDebug("会话 #{Id} 开始", id);
        return id;
    }

    public async Task CompleteSessionAsync(int sessionId, string decisionSummary, int tasksScheduled,
        string? whiteboardBefore, string? whiteboardAfter, string? rawDecisionJson)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET completed_at = CURRENT_TIMESTAMP,
                decision_summary = @summary,
                tasks_scheduled = @scheduled,
                whiteboard_before = @before,
                whiteboard_after = @after,
                raw_decision_json = @json,
                status = 'completed'
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.Parameters.AddWithValue("@summary", decisionSummary);
        cmd.Parameters.AddWithValue("@scheduled", tasksScheduled);
        cmd.Parameters.AddWithValue("@before", whiteboardBefore ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@after", whiteboardAfter ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@json", rawDecisionJson ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
        _logger?.LogInformation("会话 #{Id} 完成，安排任务: {Count}", sessionId, tasksScheduled);
    }

    public async Task FailSessionAsync(int sessionId, string errorMessage)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET completed_at = CURRENT_TIMESTAMP,
                status = 'failed',
                error_message = @error
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.Parameters.AddWithValue("@error", errorMessage);

        await cmd.ExecuteNonQueryAsync();
        _logger?.LogWarning("会话 #{Id} 失败: {Error}", sessionId, errorMessage);
    }

    public async Task UpdateSessionTaskResultsAsync(int sessionId, int succeeded, int failed)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions
            SET tasks_succeeded = @succeeded,
                tasks_failed = @failed
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.Parameters.AddWithValue("@succeeded", succeeded);
        cmd.Parameters.AddWithValue("@failed", failed);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<OrchestrationSession>> ListSessionsAsync(int limit = 20)
    {
        var results = new List<OrchestrationSession>();
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions ORDER BY id DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadSession(reader));
        }
        return results;
    }

    public async Task<OrchestrationSession?> GetSessionAsync(int id)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM sessions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return ReadSession(reader);
        }
        return null;
    }

    public async Task<int> GetTotalSessionCountAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE status = 'completed'";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> GetTotalTasksScheduledAsync()
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(tasks_scheduled), 0) FROM sessions";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // ==================== Helpers ====================

    private static AutopilotGoal ReadGoal(SqliteDataReader reader)
    {
        return new AutopilotGoal
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? "" : reader.GetString(reader.GetOrdinal("description")),
            IsActive = reader.GetInt32(reader.GetOrdinal("is_active")) == 1,
            CreatedAt = reader.GetString(reader.GetOrdinal("created_at")),
            UpdatedAt = reader.GetString(reader.GetOrdinal("updated_at"))
        };
    }

    private static Whiteboard ReadWhiteboard(SqliteDataReader reader)
    {
        return new Whiteboard
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            Content = reader.GetString(reader.GetOrdinal("content")),
            Version = reader.GetInt32(reader.GetOrdinal("version")),
            CreatedAt = reader.GetString(reader.GetOrdinal("created_at")),
            UpdatedAt = reader.GetString(reader.GetOrdinal("updated_at"))
        };
    }

    private static OrchestrationSession ReadSession(SqliteDataReader reader)
    {
        return new OrchestrationSession
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            TriggeredAt = reader.GetString(reader.GetOrdinal("triggered_at")),
            CompletedAt = reader.IsDBNull(reader.GetOrdinal("completed_at")) ? null : reader.GetString(reader.GetOrdinal("completed_at")),
            DecisionSummary = reader.GetString(reader.GetOrdinal("decision_summary")),
            TasksScheduled = reader.GetInt32(reader.GetOrdinal("tasks_scheduled")),
            TasksSucceeded = reader.GetInt32(reader.GetOrdinal("tasks_succeeded")),
            TasksFailed = reader.GetInt32(reader.GetOrdinal("tasks_failed")),
            WhiteboardBefore = reader.IsDBNull(reader.GetOrdinal("whiteboard_before")) ? null : reader.GetString(reader.GetOrdinal("whiteboard_before")),
            WhiteboardAfter = reader.IsDBNull(reader.GetOrdinal("whiteboard_after")) ? null : reader.GetString(reader.GetOrdinal("whiteboard_after")),
            RawDecisionJson = reader.IsDBNull(reader.GetOrdinal("raw_decision_json")) ? null : reader.GetString(reader.GetOrdinal("raw_decision_json")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message"))
        };
    }
}
