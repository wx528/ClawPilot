using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// Kimi Code CLI 执行器
/// 月之暗面 Kimi 本地助手命令行工具
/// 
/// 非交互调用: kimi --quiet -p "message"
/// JSON 流式:  kimi --print --output-format stream-json -p "message"
/// 
/// 参考: https://moonshotai.github.io/kimi-cli/
/// </summary>
public class KimiCodeExecutor : CliExecutorBase
{
    protected override string CommandName => "kimi";

    public KimiCodeExecutor(ILogger<KimiCodeExecutor> logger, string commandPath)
        : base(logger, commandPath)
    {
    }

    protected override string BuildArguments(string message)
    {
        var parts = new List<string>();

        // --quiet 是 --print --output-format text --final-message-only 的快捷方式
        if (FinalMessageOnly)
        {
            parts.Add("--quiet");
        }
        else
        {
            parts.Add("--print");
            if (OutputFormat != "text")
            {
                parts.Add("--output-format");
                parts.Add(OutputFormat);
            }
        }

        // AFK 模式（--quiet 已隐含 AFK）
        if (AfkMode && !FinalMessageOnly)
        {
            parts.Add("--afk");
        }

        // 工作目录
        if (!string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            parts.Add("--work-dir");
            parts.Add(EscapeArgument(WorkingDirectory));
        }

        // 最大步数
        if (MaxStepsPerTurn > 0 && MaxStepsPerTurn != 1000)
        {
            parts.Add("--max-steps-per-turn");
            parts.Add(MaxStepsPerTurn.ToString());
        }

        // prompt
        parts.Add("-p");
        parts.Add(EscapeArgument(message));

        return JoinArgs(parts.ToArray());
    }
}
