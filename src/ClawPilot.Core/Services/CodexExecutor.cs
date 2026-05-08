using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

public class CodexExecutor : CliExecutorBase
{
    protected override string CommandName => "codex";

    public string ApprovalMode { get; set; } = "full-auto";
    public string? Model { get; set; }
    public string? ExtraArgs { get; set; }

    public CodexExecutor(ILogger<CodexExecutor> logger, string commandPath)
        : base(logger, commandPath)
    {
    }

    protected override string BuildArguments(string message)
    {
        var parts = new List<string>();

        parts.Add("-q");

        parts.Add("--approval-mode");
        parts.Add(ApprovalMode);

        if (!string.IsNullOrWhiteSpace(Model))
        {
            parts.Add("--model");
            parts.Add(EscapeArgument(Model));
        }

        if (!string.IsNullOrWhiteSpace(ExtraArgs))
        {
            parts.Add(ExtraArgs);
        }

        parts.Add(EscapeArgument(message));

        return JoinArgs(parts.ToArray());
    }
}
