using ClawPilot.Core.Services;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Tests;

public class CliExecutorBaseTests
{
    private class TestCliExecutor : CliExecutorBase
    {
        protected override string CommandName => "testcli";

        public TestCliExecutor(ILogger logger, string commandPath)
            : base(logger, commandPath) { }

        protected override string BuildArguments(string message)
        {
            return $"--quiet --message {EscapeArgument(message)}";
        }

        public string TestBuildArguments(string message) => BuildArguments(message);
        public string? TestResolveCommandPath() => ResolveCommandPath();
        public static string TestEscapeArgument(string arg) => EscapeArgument(arg);
        public static string TestJoinArgs(params string[] parts) => JoinArgs(parts);
    }

    private static TestCliExecutor CreateExecutor(string commandPath = "testcli")
    {
        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TestCliExecutor>();
        return new TestCliExecutor(logger, commandPath);
    }

    [Fact]
    public void EscapeArgument_SimpleString_WrappedInQuotes()
    {
        var result = TestCliExecutor.TestEscapeArgument("hello");
        Assert.Equal("\"hello\"", result);
    }

    [Fact]
    public void EscapeArgument_EmptyString_ReturnsEmptyQuotes()
    {
        var result = TestCliExecutor.TestEscapeArgument("");
        Assert.Equal("\"\"", result);
    }

    [Fact]
    public void EscapeArgument_NullString_ReturnsEmptyQuotes()
    {
        var result = TestCliExecutor.TestEscapeArgument(null!);
        Assert.Equal("\"\"", result);
    }

    [Fact]
    public void EscapeArgument_ContainsQuotes_EscapesQuotes()
    {
        var result = TestCliExecutor.TestEscapeArgument("say \"hello\"");
        Assert.Equal("\"say \\\"hello\\\"\"", result);
    }

    [Fact]
    public void EscapeArgument_ContainsSpaces_WrappedInQuotes()
    {
        var result = TestCliExecutor.TestEscapeArgument("hello world");
        Assert.Equal("\"hello world\"", result);
    }

    [Fact]
    public void JoinArgs_JoinsNonEmptyParts()
    {
        var result = TestCliExecutor.TestJoinArgs("--quiet", "--message", "\"test\"");
        Assert.Equal("--quiet --message \"test\"", result);
    }

    [Fact]
    public void JoinArgs_SkipsEmptyParts()
    {
        var result = TestCliExecutor.TestJoinArgs("--quiet", "", "--message", null!, "test");
        Assert.Equal("--quiet --message test", result);
    }

    [Fact]
    public void BuildArguments_IncludesMessage()
    {
        var executor = CreateExecutor();
        var args = executor.TestBuildArguments("Write unit tests");
        Assert.Contains("Write unit tests", args);
        Assert.Contains("--quiet", args);
        Assert.Contains("--message", args);
    }

    [Fact]
    public void BuildArguments_EscapesSpecialCharacters()
    {
        var executor = CreateExecutor();
        var args = executor.TestBuildArguments("Fix bug in \"main.py\"");
        Assert.Contains("\\\"main.py\\\"", args);
    }

    [Fact]
    public void ResolveCommandPath_AbsolutePathExists_ReturnsPath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var executor = CreateExecutor(tempFile);
            var result = executor.TestResolveCommandPath();
            Assert.Equal(Path.GetFullPath(tempFile), result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ResolveCommandPath_AbsolutePathNotExists_ReturnsNull()
    {
        var executor = CreateExecutor("C:\\nonexistent\\path\\testcli.exe");
        var result = executor.TestResolveCommandPath();
        Assert.Null(result);
    }

    [Fact]
    public void ResolveCommandPath_SimpleCommandName_ReturnsCommandName()
    {
        var executor = CreateExecutor("testcli");
        var result = executor.TestResolveCommandPath();
        Assert.Equal("testcli", result);
    }

    [Fact]
    public void WorkingDirectory_SetAndAccessible()
    {
        var executor = CreateExecutor();
        var tempDir = Path.GetTempPath();

        executor.WorkingDirectory = tempDir;
        Assert.Equal(tempDir, executor.WorkingDirectory);
    }

    [Fact]
    public void MaxStepsPerTurn_DefaultValue()
    {
        var executor = CreateExecutor();
        Assert.Equal(100, executor.MaxStepsPerTurn);
    }

    [Fact]
    public void AfkMode_DefaultIsTrue()
    {
        var executor = CreateExecutor();
        Assert.True(executor.AfkMode);
    }

    [Fact]
    public void OutputFormat_DefaultIsText()
    {
        var executor = CreateExecutor();
        Assert.Equal("text", executor.OutputFormat);
    }

    // ==================== Aider 参数构建测试 ====================

    private static AiderExecutor CreateAiderExecutor()
    {
        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<AiderExecutor>();
        return new AiderExecutor(logger, "aider");
    }

    [Fact]
    public void Aider_BuildArguments_IncludesMessage()
    {
        var executor = CreateAiderExecutor();
        var method = typeof(AiderExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "Fix the bug" })!;
        Assert.Contains("--message", args);
        Assert.Contains("Fix the bug", args);
    }

    [Fact]
    public void Aider_BuildArguments_YesAlwaysByDefault()
    {
        var executor = CreateAiderExecutor();
        var method = typeof(AiderExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "test" })!;
        Assert.Contains("--yes-always", args);
    }

    [Fact]
    public void Aider_BuildArguments_NoAutoCommitsByDefault()
    {
        var executor = CreateAiderExecutor();
        var method = typeof(AiderExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "test" })!;
        Assert.Contains("--no-auto-commits", args);
    }

    [Fact]
    public void Aider_BuildArguments_WithModel()
    {
        var executor = CreateAiderExecutor();
        executor.Model = "gpt-4";
        var method = typeof(AiderExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "test" })!;
        Assert.Contains("--model", args);
        Assert.Contains("gpt-4", args);
    }

    // ==================== Codex 参数构建测试 ====================

    private static CodexExecutor CreateCodexExecutor()
    {
        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<CodexExecutor>();
        return new CodexExecutor(logger, "codex");
    }

    [Fact]
    public void Codex_BuildArguments_IncludesQuietMode()
    {
        var executor = CreateCodexExecutor();
        var method = typeof(CodexExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "test" })!;
        Assert.Contains("-q", args);
    }

    [Fact]
    public void Codex_BuildArguments_DefaultApprovalMode()
    {
        var executor = CreateCodexExecutor();
        var method = typeof(CodexExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "test" })!;
        Assert.Contains("--approval-mode", args);
        Assert.Contains("full-auto", args);
    }

    [Fact]
    public void Codex_BuildArguments_WithModel()
    {
        var executor = CreateCodexExecutor();
        executor.Model = "o4-mini";
        var method = typeof(CodexExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "test" })!;
        Assert.Contains("--model", args);
        Assert.Contains("o4-mini", args);
    }

    // ==================== QwenCode 参数构建测试 ====================

    private static QwenCodeExecutor CreateQwenCodeExecutor()
    {
        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<QwenCodeExecutor>();
        return new QwenCodeExecutor(logger, "qwen-code");
    }

    [Fact]
    public void QwenCode_BuildArguments_IncludesMessage()
    {
        var executor = CreateQwenCodeExecutor();
        var method = typeof(QwenCodeExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "Refactor code" })!;
        Assert.Contains("--message", args);
        Assert.Contains("Refactor code", args);
    }

    [Fact]
    public void QwenCode_BuildArguments_YesByDefault()
    {
        var executor = CreateQwenCodeExecutor();
        var method = typeof(QwenCodeExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "test" })!;
        Assert.Contains("--yes", args);
    }

    [Fact]
    public void QwenCode_BuildArguments_WithModel()
    {
        var executor = CreateQwenCodeExecutor();
        executor.Model = "qwen-max";
        var method = typeof(QwenCodeExecutor).GetMethod("BuildArguments",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var args = (string)method!.Invoke(executor, new object[] { "test" })!;
        Assert.Contains("--model", args);
        Assert.Contains("qwen-max", args);
    }
}
