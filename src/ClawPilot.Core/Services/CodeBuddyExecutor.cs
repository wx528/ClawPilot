using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// CodeBuddy Code CLI 执行器
/// 腾讯云 AI 编程助手命令行工具（WorkBuddy / CodeBuddy Code）
/// 
/// 非交互调用: codebuddy -p "message" --dangerously-skip-permissions
/// JSON 输出:  codebuddy -p "message" --output-format json --dangerously-skip-permissions
/// 
/// 安装: npm install -g @tencent-ai/codebuddy-code
/// 参考: https://www.codebuddy.cn/docs/cli/headless
/// </summary>
public class CodeBuddyExecutor : CliExecutorBase
{
    protected override string CommandName => "codebuddy";
    public override TaskType SupportedTaskType => TaskType.CodeBuddy;
    public override string Name => "codebuddy";

    /// <summary>
    /// 是否跳过权限确认（无人值守必需，对应 --dangerously-skip-permissions）
    /// </summary>
    public bool SkipPermissions { get; set; } = true;

    /// <summary>
    /// 追加系统提示词（对应 --append-system-prompt，仅与 --print 配合使用）
    /// </summary>
    public string? AppendSystemPrompt { get; set; }

    /// <summary>
    /// 允许使用的工具白名单（对应 --tools，逗号分隔）
    /// 例如: "Bash,Read,Edit,Write" 或 "default"
    /// </summary>
    public string? AllowedTools { get; set; }

    /// <summary>
    /// 禁止使用的工具黑名单（对应 --disallowedTools）
    /// </summary>
    public string? DisallowedTools { get; set; }

    public CodeBuddyExecutor(ILogger<CodeBuddyExecutor> logger, string commandPath)
        : base(logger, commandPath)
    {
    }

    protected override string BuildArguments(string message)
    {
        var parts = new List<string>();

        // -p / --print: 非交互模式
        parts.Add("-p");

        // 输出格式
        if (OutputFormat != "text")
        {
            parts.Add("--output-format");
            parts.Add(OutputFormat);
        }

        // 跳过权限确认（无人值守必需）
        if (SkipPermissions)
        {
            parts.Add("--dangerously-skip-permissions");
        }

        // 工作目录（codebuddy 通过 cwd 指定，不支持 --work-dir 参数）
        // WorkingDirectory 由基类通过 ProcessStartInfo.WorkingDirectory 传入

        // 追加系统提示词
        if (!string.IsNullOrWhiteSpace(AppendSystemPrompt))
        {
            parts.Add("--append-system-prompt");
            parts.Add(EscapeArgument(AppendSystemPrompt));
        }

        // 允许的工具白名单
        if (!string.IsNullOrWhiteSpace(AllowedTools))
        {
            parts.Add("--tools");
            parts.Add(EscapeArgument(AllowedTools));
        }

        // 禁止的工具黑名单
        if (!string.IsNullOrWhiteSpace(DisallowedTools))
        {
            parts.Add("--disallowedTools");
            parts.Add(EscapeArgument(DisallowedTools));
        }

        // prompt（放在最后）
        parts.Add(EscapeArgument(message));

        return JoinArgs(parts.ToArray());
    }
}
