using ClawPilot.Core.Services;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Tests;

public class OrchestratorStorageServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly OrchestratorStorageService _service;

    public OrchestratorStorageServiceTests()
    {
        _dbPath = Path.GetTempFileName();
        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<OrchestratorStorageService>();
        _service = new OrchestratorStorageService(_dbPath, logger);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); }
        catch { }
    }

    private async Task InitAsync()
    {
        await _service.EnsureTablesExistAsync();
    }

    // ==================== 表初始化 ====================

    [Fact]
    public async Task EnsureTablesExistAsync_CreatesTables()
    {
        await _service.EnsureTablesExistAsync();

        var goal = await _service.GetActiveGoalAsync();
        Assert.Null(goal);
    }

    [Fact]
    public async Task EnsureTablesExistAsync_CreatesDefaultWhiteboard()
    {
        await _service.EnsureTablesExistAsync();

        var wb = await _service.GetLatestWhiteboardAsync();
        Assert.NotNull(wb);
        Assert.Equal(1, wb.Version);
        Assert.False(string.IsNullOrEmpty(wb.Content));
    }

    [Fact]
    public async Task EnsureTablesExistAsync_Idempotent()
    {
        await _service.EnsureTablesExistAsync();
        await _service.EnsureTablesExistAsync();

        var wb = await _service.GetLatestWhiteboardAsync();
        Assert.NotNull(wb);
    }

    // ==================== Goals CRUD ====================

    [Fact]
    public async Task CreateGoalAsync_ReturnsId()
    {
        await InitAsync();

        var id = await _service.CreateGoalAsync("Test Goal", "Test description");

        Assert.True(id > 0);
    }

    [Fact]
    public async Task GetActiveGoalAsync_ReturnsActiveGoal()
    {
        await InitAsync();

        await _service.CreateGoalAsync("Active Goal", "Active description");
        var goal = await _service.GetActiveGoalAsync();

        Assert.NotNull(goal);
        Assert.Equal("Active Goal", goal!.Title);
        Assert.Equal("Active description", goal.Description);
        Assert.True(goal.IsActive);
    }

    [Fact]
    public async Task GetActiveGoalAsync_NoActiveGoal_ReturnsNull()
    {
        await InitAsync();

        var goal = await _service.GetActiveGoalAsync();
        Assert.Null(goal);
    }

    [Fact]
    public async Task ListGoalsAsync_ReturnsAllGoals()
    {
        await InitAsync();

        await _service.CreateGoalAsync("Goal 1");
        await _service.CreateGoalAsync("Goal 2");
        await _service.CreateGoalAsync("Goal 3");

        var goals = await _service.ListGoalsAsync();
        Assert.Equal(3, goals.Count);
    }

    [Fact]
    public async Task UpdateGoalAsync_UpdatesTitle()
    {
        await InitAsync();

        var id = await _service.CreateGoalAsync("Original");
        var updated = await _service.UpdateGoalAsync(id, title: "Updated");

        Assert.True(updated);
        var goal = await _service.GetActiveGoalAsync();
        Assert.Equal("Updated", goal!.Title);
    }

    [Fact]
    public async Task UpdateGoalAsync_DeactivatesGoal()
    {
        await InitAsync();

        var id = await _service.CreateGoalAsync("Active Goal");
        await _service.UpdateGoalAsync(id, isActive: false);

        var goal = await _service.GetActiveGoalAsync();
        Assert.Null(goal);
    }

    [Fact]
    public async Task UpdateGoalAsync_NoChanges_ReturnsFalse()
    {
        await InitAsync();

        var id = await _service.CreateGoalAsync("Goal");
        var result = await _service.UpdateGoalAsync(id);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteGoalAsync_RemovesGoal()
    {
        await InitAsync();

        var id = await _service.CreateGoalAsync("To Delete");
        var deleted = await _service.DeleteGoalAsync(id);

        Assert.True(deleted);
        var goals = await _service.ListGoalsAsync();
        Assert.Empty(goals);
    }

    [Fact]
    public async Task DeleteGoalAsync_NonexistentId_ReturnsFalse()
    {
        await InitAsync();

        var deleted = await _service.DeleteGoalAsync(9999);
        Assert.False(deleted);
    }

    // ==================== Whiteboard ====================

    [Fact]
    public async Task GetLatestWhiteboardAsync_ReturnsLatest()
    {
        await InitAsync();

        var wb1 = await _service.UpdateWhiteboardAsync("First update");
        var wb2 = await _service.UpdateWhiteboardAsync("Second update");

        var latest = await _service.GetLatestWhiteboardAsync();
        Assert.Equal("Second update", latest.Content);
        Assert.Equal(wb1.Version + 1, wb2.Version);
    }

    [Fact]
    public async Task UpdateWhiteboardAsync_IncrementsVersion()
    {
        await InitAsync();

        var wb1 = await _service.UpdateWhiteboardAsync("Version 1");
        var wb2 = await _service.UpdateWhiteboardAsync("Version 2");
        var wb3 = await _service.UpdateWhiteboardAsync("Version 3");

        Assert.Equal(wb1.Version + 1, wb2.Version);
        Assert.Equal(wb2.Version + 1, wb3.Version);
    }

    [Fact]
    public async Task UpdateWhiteboardAsync_ReturnsNewWhiteboard()
    {
        await InitAsync();

        var wb = await _service.UpdateWhiteboardAsync("New content");

        Assert.Equal("New content", wb.Content);
        Assert.True(wb.Id > 0);
    }

    // ==================== Sessions ====================

    [Fact]
    public async Task BeginSessionAsync_ReturnsSessionId()
    {
        await InitAsync();

        var id = await _service.BeginSessionAsync();
        Assert.True(id > 0);
    }

    [Fact]
    public async Task CompleteSessionAsync_UpdatesSession()
    {
        await InitAsync();

        var id = await _service.BeginSessionAsync();
        await _service.CompleteSessionAsync(id, "Test decision", 3, "wb before", "wb after", "{}");

        var session = await _service.GetSessionAsync(id);
        Assert.NotNull(session);
        Assert.Equal("completed", session!.Status);
        Assert.Equal("Test decision", session.DecisionSummary);
        Assert.Equal(3, session.TasksScheduled);
    }

    [Fact]
    public async Task FailSessionAsync_UpdatesSessionWithError()
    {
        await InitAsync();

        var id = await _service.BeginSessionAsync();
        await _service.FailSessionAsync(id, "API timeout");

        var session = await _service.GetSessionAsync(id);
        Assert.NotNull(session);
        Assert.Equal("failed", session!.Status);
        Assert.Equal("API timeout", session.ErrorMessage);
    }

    [Fact]
    public async Task UpdateSessionTaskResultsAsync_UpdatesCounts()
    {
        await InitAsync();

        var id = await _service.BeginSessionAsync();
        await _service.CompleteSessionAsync(id, "decision", 5, null, null, null);
        await _service.UpdateSessionTaskResultsAsync(id, 3, 2);

        var session = await _service.GetSessionAsync(id);
        Assert.NotNull(session);
        Assert.Equal(3, session!.TasksSucceeded);
        Assert.Equal(2, session.TasksFailed);
    }

    [Fact]
    public async Task ListSessionsAsync_ReturnsOrderedByDesc()
    {
        await InitAsync();

        var id1 = await _service.BeginSessionAsync();
        var id2 = await _service.BeginSessionAsync();
        var id3 = await _service.BeginSessionAsync();

        var sessions = await _service.ListSessionsAsync();
        Assert.Equal(3, sessions.Count);
        Assert.Equal(id3, sessions[0].Id);
        Assert.Equal(id2, sessions[1].Id);
        Assert.Equal(id1, sessions[2].Id);
    }

    [Fact]
    public async Task ListSessionsAsync_RespectsLimit()
    {
        await InitAsync();

        for (int i = 0; i < 5; i++)
            await _service.BeginSessionAsync();

        var sessions = await _service.ListSessionsAsync(limit: 3);
        Assert.Equal(3, sessions.Count);
    }

    [Fact]
    public async Task GetTotalSessionCountAsync_ReturnsCompletedOnly()
    {
        await InitAsync();

        var id1 = await _service.BeginSessionAsync();
        var id2 = await _service.BeginSessionAsync();
        await _service.CompleteSessionAsync(id1, "done", 1, null, null, null);

        var count = await _service.GetTotalSessionCountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetTotalTasksScheduledAsync_SumsAllSessions()
    {
        await InitAsync();

        var id1 = await _service.BeginSessionAsync();
        await _service.CompleteSessionAsync(id1, "d1", 3, null, null, null);

        var id2 = await _service.BeginSessionAsync();
        await _service.CompleteSessionAsync(id2, "d2", 5, null, null, null);

        var total = await _service.GetTotalTasksScheduledAsync();
        Assert.Equal(8, total);
    }

    [Fact]
    public async Task GetSessionAsync_NonexistentId_ReturnsNull()
    {
        await InitAsync();

        var session = await _service.GetSessionAsync(9999);
        Assert.Null(session);
    }
}
