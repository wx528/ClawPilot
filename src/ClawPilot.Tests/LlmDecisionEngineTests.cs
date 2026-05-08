using ClawPilot.Core.Models;
using ClawPilot.Core.Services;
using Moq;

namespace ClawPilot.Tests;

public class LlmDecisionEngineTests
{
    private readonly Mock<ILlmClient> _mockLlmClient;
    private readonly LlmDecisionEngine _engine;

    public LlmDecisionEngineTests()
    {
        _mockLlmClient = new Mock<ILlmClient>();
        _engine = new LlmDecisionEngine(_mockLlmClient.Object);
    }

    // ==================== Autopilot 决策测试 ====================

    [Fact]
    public async Task DecideAutopilotAsync_ValidJson_ReturnsDecision()
    {
        var goal = new AutopilotGoal { Title = "Test Mission", Description = "Test description" };
        var whiteboard = new Whiteboard { Content = "Initial state" };
        var recentResults = new List<TaskItem>();

        var llmResponse = @"{
            ""decision_type"": ""add_tasks"",
            ""reasoning"": ""Starting mission"",
            ""tasks_to_add"": [
                {
                    ""persona_name"": ""main"",
                    ""message"": ""Check system status"",
                    ""task_type"": ""openclaw"",
                    ""priority"": ""normal"",
                    ""reason"": ""Initial check""
                }
            ],
            ""whiteboard_update"": ""Mission started, initial check scheduled"",
            ""next_interval_minutes"": 45
        }";

        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmResponse);

        var result = await _engine.DecideAutopilotAsync(
            goal, whiteboard, recentResults, TimeSpan.FromMinutes(0), DateTime.Now.AddHours(1));

        Assert.NotNull(result);
        Assert.Single(result!.TasksToAdd);
        Assert.Equal("Mission started, initial check scheduled", result.WhiteboardUpdate);
        Assert.Equal(45, result.NextIntervalMinutes);
    }

    [Fact]
    public async Task DecideAutopilotAsync_WhiteboardTruncated_WhenTooLong()
    {
        var goal = new AutopilotGoal { Title = "Test" };
        var whiteboard = new Whiteboard { Content = "test" };
        var recentResults = new List<TaskItem>();

        var longUpdate = new string('A', 10000);
        var llmResponse = $@"{{
            ""decision_type"": ""add_tasks"",
            ""reasoning"": ""test"",
            ""tasks_to_add"": [],
            ""whiteboard_update"": ""{longUpdate}""
        }}";

        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmResponse);

        var result = await _engine.DecideAutopilotAsync(
            goal, whiteboard, recentResults, TimeSpan.Zero, DateTime.Now.AddHours(1));

        Assert.NotNull(result);
        Assert.True(result!.WhiteboardUpdate.Length <= 8192);
    }

    [Fact]
    public async Task DecideAutopilotAsync_InvalidJson_ReturnsNull()
    {
        var goal = new AutopilotGoal { Title = "Test" };
        var whiteboard = new Whiteboard { Content = "test" };

        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Not valid JSON without braces");

        var result = await _engine.DecideAutopilotAsync(
            goal, whiteboard, [], TimeSpan.Zero, DateTime.Now.AddHours(1));

        Assert.Null(result);
    }

    [Fact]
    public async Task DecideAutopilotAsync_WithRecentResults_IncludesTaskInfo()
    {
        var goal = new AutopilotGoal { Title = "Test" };
        var whiteboard = new Whiteboard { Content = "test" };
        var recentResults = new List<TaskItem>
        {
            new() { Id = 1, AgentName = "main", Message = "Task 1", Status = ClawPilot.Core.Models.TaskStatus.Success, Output = "OK" },
            new() { Id = 2, AgentName = "main", Message = "Task 2", Status = ClawPilot.Core.Models.TaskStatus.Failed, Output = "Error" }
        };

        var llmResponse = @"{
            ""decision_type"": ""add_tasks"",
            ""reasoning"": ""Based on results"",
            ""tasks_to_add"": [],
            ""whiteboard_update"": ""Updated""
        }";

        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmResponse);

        var result = await _engine.DecideAutopilotAsync(
            goal, whiteboard, recentResults, TimeSpan.FromMinutes(60), DateTime.Now.AddHours(1));

        Assert.NotNull(result);
    }

    [Fact]
    public async Task DecideAutopilotAsync_WithPersonaPrompt_PassesToLlm()
    {
        var goal = new AutopilotGoal { Title = "Test" };
        var whiteboard = new Whiteboard { Content = "test" };
        var personaPrompt = "You are a tech news curator.";

        var llmResponse = @"{
            ""decision_type"": ""add_tasks"",
            ""reasoning"": ""test"",
            ""tasks_to_add"": [],
            ""whiteboard_update"": ""updated""
        }";

        string? capturedSystemPrompt = null;
        _mockLlmClient
            .Setup(c => c.ChatCompletionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, double, CancellationToken>((sys, user, _, _, _) => capturedSystemPrompt = sys)
            .ReturnsAsync(llmResponse);

        await _engine.DecideAutopilotAsync(
            goal, whiteboard, [], TimeSpan.Zero, DateTime.Now.AddHours(1),
            personaPrompt: personaPrompt);

        Assert.NotNull(capturedSystemPrompt);
        Assert.Contains(personaPrompt, capturedSystemPrompt);
    }

    // ==================== 审核报告解析测试 ====================

    [Fact]
    public void ParseReviewOutput_PassResult_ReturnsPassed()
    {
        var output = "总体结果: PASS\n总结: 代码质量良好，所有测试通过";

        var result = _engine.ParseReviewOutput(output);

        Assert.True(result.Passed);
        Assert.Equal("代码质量良好，所有测试通过", result.Summary);
    }

    [Fact]
    public void ParseReviewOutput_FailResult_ReturnsFailed()
    {
        var output = "总体结果: FAIL\n总结: 存在严重问题\n问题1: 缺少单元测试\n问题2: 代码风格不一致";

        var result = _engine.ParseReviewOutput(output);

        Assert.False(result.Passed);
        Assert.Equal("存在严重问题", result.Summary);
        Assert.Equal(2, result.Issues.Count);
    }

    [Fact]
    public void ParseReviewOutput_EmptyOutput_ReturnsFailed()
    {
        var result = _engine.ParseReviewOutput("");

        Assert.False(result.Passed);
        Assert.Equal("No output from reviewer", result.Summary);
    }

    [Fact]
    public void ParseReviewOutput_NullOutput_ReturnsFailed()
    {
        var result = _engine.ParseReviewOutput(null);

        Assert.False(result.Passed);
        Assert.Equal("No output from reviewer", result.Summary);
    }

    [Fact]
    public void ParseReviewOutput_EmojiPass_ReturnsPassed()
    {
        var output = "总体结果: ✅\n总结: 审核通过";

        var result = _engine.ParseReviewOutput(output);

        Assert.True(result.Passed);
    }

    [Fact]
    public void ParseReviewOutput_ChinesePass_ReturnsPassed()
    {
        var output = "总体结果: 通过\n总结: 代码符合规范";

        var result = _engine.ParseReviewOutput(output);

        Assert.True(result.Passed);
    }

    [Fact]
    public void ParseReviewOutput_IssueExtraction_Works()
    {
        var output = "RESULT: FAIL\nSummary: Multiple issues found\nIssue 1: Missing error handling\nIssue 2: Memory leak in loop\nIssue 3: Hardcoded credentials";

        var result = _engine.ParseReviewOutput(output);

        Assert.False(result.Passed);
        Assert.Equal(3, result.Issues.Count);
        Assert.Contains("Missing error handling", result.Issues[0]);
    }

    [Fact]
    public void ParseReviewOutput_RawOutputPreserved()
    {
        var output = "RESULT: PASS\nSummary: Good";

        var result = _engine.ParseReviewOutput(output);

        Assert.Equal(output, result.RawOutput);
    }

    [Fact]
    public void ParseReviewOutput_NoExplicitResult_DefaultsToPass()
    {
        var output = "代码看起来不错，结构清晰，命名规范。";

        var result = _engine.ParseReviewOutput(output);

        Assert.True(result.Passed);
    }

    [Fact]
    public void ParseReviewOutput_FailWithoutIssues_ExtractsBulletPoints()
    {
        var output = "RESULT: FAIL\n- Variable naming is inconsistent across modules\n- Missing documentation for public APIs\n- Test coverage below 50%";

        var result = _engine.ParseReviewOutput(output);

        Assert.False(result.Passed);
        Assert.True(result.Issues.Count > 0);
    }
}
