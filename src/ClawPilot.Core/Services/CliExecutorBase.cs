using System.Diagnostics;
using System.Text;
using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// 通用 CLI 执行器基类
/// 抽象出 AI Code CLI 工具的公共逻辑：PATH 查找、进程启动、超时控制、输出收集
/// 
/// 子类只需定义：
///   - CommandName: 可执行文件名（如 "kimi", "codebuddy"）
///   - BuildNonInteractiveArgs(): 构建非交互模式参数
///   - （可选）AdditionalEnvironmentVariables: 额外环境变量
/// 
/// 接入新 CLI 工具只需继承此类，实现 2 个抽象成员即可
/// </summary>
public abstract class CliExecutorBase : IExecutor
{
    protected readonly ILogger Logger;
    protected readonly string CommandPath;

    public abstract TaskType SupportedTaskType { get; }
    public abstract string Name { get; }

    /// <summary>
    /// CLI 执行的工作目录
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// 单轮最大步数（对应各 CLI 的 --max-steps-per-turn 或类似参数）
    /// </summary>
    public int MaxStepsPerTurn { get; set; } = 100;

    /// <summary>
    /// 是否使用无人值守模式（自动审批 + 自动 dismiss）
    /// </summary>
    public bool AfkMode { get; set; } = true;

    /// <summary>
    /// 输出格式：text 或 stream-json
    /// </summary>
    public string OutputFormat { get; set; } = "text";

    /// <summary>
    /// 是否仅输出最终消息
    /// </summary>
    public bool FinalMessageOnly { get; set; } = true;

    /// <summary>
    /// 额外环境变量（子类可覆盖）
    /// </summary>
    protected virtual Dictionary<string, string> AdditionalEnvironmentVariables => new();

    /// <summary>
    /// CLI 可执行文件名（如 "kimi", "codebuddy", "aider"）
    /// </summary>
    protected abstract string CommandName { get; }

    protected CliExecutorBase(ILogger logger, string commandPath)
    {
        Logger = logger;
        CommandPath = commandPath;
    }

    async Task<ExecutorResult> IExecutor.ExecuteAsync(string agentName, string message, int timeoutSeconds, CancellationToken ct)
    {
        var (success, output, error) = await ExecuteAsync(message, timeoutSeconds);
        return new ExecutorResult
        {
            Success = success,
            Output = output,
            Error = error,
            ExitCode = success ? 0 : 1
        };
    }

    public async Task<ExecutorHealthCheckResult> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var (available, version) = await CheckAvailabilityAsync();
            return new ExecutorHealthCheckResult
            {
                IsHealthy = available,
                ExecutorName = Name,
                TaskType = SupportedTaskType,
                Message = available ? $"{CommandName} CLI 可用" : $"{CommandName} CLI 未找到或不可用",
                Version = string.IsNullOrEmpty(version) ? null : version
            };
        }
        catch (Exception ex)
        {
            return new ExecutorHealthCheckResult
            {
                IsHealthy = false,
                ExecutorName = Name,
                TaskType = SupportedTaskType,
                Message = $"健康检查失败: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 执行 CLI 任务
    /// </summary>
    public async Task<(bool Success, string Output, string Error)> ExecuteAsync(string message, int timeoutSeconds = 300)
    {
        try
        {
            Logger.LogInformation("开始执行 {Command} 任务...", CommandName);

            var resolvedPath = ResolveCommandPath();
            if (resolvedPath == null)
            {
                var err = $"{CommandName} CLI 未找到: 已尝试 '{CommandPath}' 及 PATH 查找";
                Logger.LogError(err);
                return (false, string.Empty, err);
            }

            Logger.LogDebug("使用 {Command} CLI 路径: {Path}", CommandName, resolvedPath);

            var args = BuildArguments(message);
            Logger.LogDebug("{Command} CLI 参数: {Args}", CommandName, args);

            // Windows 中文编码修复：通过 cmd.exe 设置代码页为 UTF-8 (65001)
            // 否则 Node.js CLI 工具（kimi, codebuddy）输出中文会乱码
            var startInfo = new ProcessStartInfo();
            if (OperatingSystem.IsWindows())
            {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c chcp 65001 >nul && \"{resolvedPath}\" {args}";
            }
            else
            {
                startInfo.FileName = resolvedPath;
                startInfo.Arguments = args;
            }
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.CreateNoWindow = true;

            if (!string.IsNullOrWhiteSpace(WorkingDirectory) && Directory.Exists(WorkingDirectory))
            {
                startInfo.WorkingDirectory = WorkingDirectory;
            }

            // 通用环境变量
            startInfo.EnvironmentVariables["NO_COLOR"] = "1";

            // 子类额外的环境变量
            foreach (var kv in AdditionalEnvironmentVariables)
            {
                startInfo.EnvironmentVariables[kv.Key] = kv.Value;
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (false, string.Empty, $"无法启动 {CommandName} CLI 进程");
            }

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            var outputTask = Task.Run(async () =>
            {
                using var reader = process.StandardOutput;
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null) outputBuilder.AppendLine(line);
                }
            });

            var errorTask = Task.Run(async () =>
            {
                using var reader = process.StandardError;
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null) errorBuilder.AppendLine(line);
                }
            });

            var timeoutMs = timeoutSeconds * 1000;
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(process.WaitForExitAsync(), timeoutTask);

            if (completedTask == timeoutTask)
            {
                Logger.LogWarning("{Command} 任务超时 ({Timeout}s)，尝试终止进程", CommandName, timeoutSeconds);
                try { process.Kill(entireProcessTree: true); } catch { }

                await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5));
                var partialOutput = outputBuilder.ToString();
                return (false, partialOutput, $"执行超时 ({timeoutSeconds}s)");
            }

            await Task.WhenAll(outputTask, errorTask);

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();

            Logger.LogInformation("{Command} 任务执行完成，ExitCode: {ExitCode}", CommandName, process.ExitCode);
            return (process.ExitCode == 0, output, error);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "执行 {Command} 任务失败", CommandName);
            return (false, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// 从任务项执行
    /// </summary>
    public async Task<(bool Success, string Output, string Error)> ExecuteTaskAsync(TaskItem task, int timeoutSeconds = 300)
    {
        return await ExecuteAsync(task.Message, timeoutSeconds);
    }

    /// <summary>
    /// 检查 CLI 是否可用
    /// </summary>
    public async Task<(bool Available, string Version)> CheckAvailabilityAsync()
    {
        try
        {
            var resolvedPath = ResolveCommandPath();
            if (resolvedPath == null)
            {
                return (false, string.Empty);
            }

            var startInfo = new ProcessStartInfo();
            if (OperatingSystem.IsWindows())
            {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/c chcp 65001 >nul && \"{resolvedPath}\" --version";
            }
            else
            {
                startInfo.FileName = resolvedPath;
                startInfo.Arguments = "--version";
            }
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.CreateNoWindow = true;

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (false, string.Empty);
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (process.ExitCode == 0, output.Trim());
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    /// <summary>
    /// 构建完整的命令行参数（子类可覆盖以自定义参数构建逻辑）
    /// </summary>
    protected abstract string BuildArguments(string message);

    /// <summary>
    /// 解析命令路径：支持绝对路径、相对路径和 PATH 中的命令
    /// </summary>
    protected virtual string? ResolveCommandPath()
    {
        // 1. 绝对路径且文件存在
        if (Path.IsPathRooted(CommandPath) && File.Exists(CommandPath))
        {
            return CommandPath;
        }

        // 2. 不含路径分隔符的命令名，在 PATH 中查找
        if (!Path.IsPathRooted(CommandPath)
            && !CommandPath.Contains(Path.DirectorySeparatorChar)
            && !CommandPath.Contains(Path.AltDirectorySeparatorChar))
        {
            var pathFromWhere = FindInPath(CommandPath);
            if (pathFromWhere != null) return pathFromWhere;

            // where 没找到，返回原始命令名（Process.Start 可能自己能找到）
            return CommandPath;
        }

        // 3. 相对路径
        if (File.Exists(CommandPath))
        {
            return Path.GetFullPath(CommandPath);
        }

        return null;
    }

    /// <summary>
    /// 在系统 PATH 中查找可执行文件（Windows: where, Linux/Mac: which）
    /// </summary>
    protected static string? FindInPath(string commandName)
    {
        try
        {
            var finder = OperatingSystem.IsWindows() ? "where" : "which";
            var startInfo = new ProcessStartInfo
            {
                FileName = finder,
                Arguments = commandName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                var firstPath = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(firstPath) && File.Exists(firstPath))
                {
                    return firstPath;
                }
            }
        }
        catch { /* where/which 不可用 */ }

        return null;
    }

    /// <summary>
    /// 转义命令行参数
    /// </summary>
    protected static string EscapeArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        var escaped = arg.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    /// <summary>
    /// 辅助：构建参数列表并拼接为字符串
    /// </summary>
    protected static string JoinArgs(params string[] parts)
    {
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
