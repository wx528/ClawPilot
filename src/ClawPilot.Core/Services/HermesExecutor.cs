using System.Diagnostics;
using System.Text;
using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Core.Services;

/// <summary>
/// Hermes 本地执行器
/// </summary>
public class HermesExecutor
{
    private readonly ILogger<HermesExecutor> _logger;
    private readonly string _commandPath;

    public HermesExecutor(ILogger<HermesExecutor> logger, string commandPath)
    {
        _logger = logger;
        _commandPath = commandPath;
    }

    /// <summary>
    /// 执行 Hermes 任务
    /// </summary>
    public async Task<(bool Success, string Output, string Error)> ExecuteAsync(string message, int timeoutSeconds = 60)
    {
        try
        {
            _logger.LogInformation("开始执行 Hermes 任务...");

            if (!File.Exists(_commandPath))
            {
                var err = $"Hermes 脚本不存在: {_commandPath}";
                _logger.LogError(err);
                return (false, string.Empty, err);
            }

            // 转义消息中的特殊字符
            var escapedMessage = message.Replace("\"", "`\"").Replace("$", "`$");

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{_commandPath}\" --quiet -q \"{escapedMessage}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = false
            };

            // 设置环境变量以避免 prompt_toolkit 的控制台问题
            startInfo.EnvironmentVariables["PROMPT_TOOLKIT_NO_WIN32"] = "1";
            startInfo.EnvironmentVariables["PROMPT_TOOLKIT_NO_COLOR"] = "1";
            startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            startInfo.EnvironmentVariables["TERM"] = "dumb";
            startInfo.EnvironmentVariables["NO_COLOR"] = "1";

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (false, string.Empty, "无法启动 PowerShell 进程");
            }

            var output = string.Empty;
            var error = string.Empty;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var timeoutTask = Task.Delay(timeoutSeconds * 1000);

            var completedTask = await Task.WhenAny(process.WaitForExitAsync(), timeoutTask);

            if (completedTask == timeoutTask)
            {
                process.Kill();
                return (false, output, "执行超时");
            }

            output = await outputTask;
            error = await errorTask;

            _logger.LogInformation("Hermes 任务执行完成");
            return (process.ExitCode == 0, output, error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行 Hermes 任务失败");
            return (false, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// 从任务项执行
    /// </summary>
    public async Task<(bool Success, string Output, string Error)> ExecuteTaskAsync(TaskItem task, int timeoutSeconds = 60)
    {
        return await ExecuteAsync(task.Message, timeoutSeconds);
    }
}