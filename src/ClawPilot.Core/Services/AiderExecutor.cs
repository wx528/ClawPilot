using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

public class AiderExecutor : CliExecutorBase
{
    protected override string CommandName => "aider";

    public bool YesAlways { get; set; } = true;
    public bool NoAutoCommits { get; set; } = true;
    public string? Model { get; set; }
    public string? ExtraArgs { get; set; }

    public AiderExecutor(ILogger<AiderExecutor> logger, string commandPath)
        : base(logger, commandPath)
    {
    }

    protected override string BuildArguments(string message)
    {
        var parts = new List<string>();

        parts.Add("--message");
        parts.Add(EscapeArgument(message));

        if (YesAlways)
        {
            parts.Add("--yes-always");
        }

        if (NoAutoCommits)
        {
            parts.Add("--no-auto-commits");
        }

        if (!string.IsNullOrWhiteSpace(Model))
        {
            parts.Add("--model");
            parts.Add(EscapeArgument(Model));
        }

        if (!string.IsNullOrWhiteSpace(ExtraArgs))
        {
            parts.Add(ExtraArgs);
        }

        return JoinArgs(parts.ToArray());
    }
}
