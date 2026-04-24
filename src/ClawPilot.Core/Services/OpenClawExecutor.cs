using ClawPilot.Core.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace ClawPilot.Core.Services;

/// <summary>
/// OpenClaw 执行器 — 通过 CLI 命令调用 OpenClaw
/// 使用 cmd.exe /C 执行，兼容 .cmd / .bat 等 PATH 中的脚本
/// </summary>
public class OpenClawExecutor
{
    private readonly string _cliCommand;
    private readonly ILogger? _logger;

    public OpenClawExecutor(string cliCommand, ILogger? logger = null)
    {
        _cliCommand = cliCommand;
        _logger = logger;
    }

    public async Task<(string Status, string Output)> ExecuteAsync(string agentName, string message, int timeoutSeconds, CancellationToken ct)
    {
        _logger?.LogInformation("调用 OpenClaw: {AgentName} - {Message}", agentName, message);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 使用 cmd.exe /C 执行，确保能找到 PATH 中的 .cmd / .bat 脚本
            var cliArgs = $"agent --agent \"{agentName}\" --message \"{message}\"";
            var arguments = $"/C {_cliCommand} {cliArgs}";
            _logger?.LogDebug("启动 OpenClaw 进程: cmd.exe {Args}", arguments);
            
            var psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            using var process = new Process { StartInfo = psi };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.Start();
            _logger?.LogDebug("进程已启动，ID: {ProcessId}", process.Id);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _logger?.LogDebug("等待进程退出，超时: {Timeout}s", timeoutSeconds);
            await process.WaitForExitAsync(linkedCts.Token);

            stopwatch.Stop();
            _logger?.LogDebug("进程已退出，退出码: {ExitCode}，耗时: {Ms}ms", process.ExitCode, stopwatch.ElapsedMilliseconds);
            var output = outputBuilder.ToString().Trim();
            var error = errorBuilder.ToString().Trim();
            _logger?.LogDebug("标准输出长度: {OutputLen}，标准错误长度: {ErrorLen}", output.Length, error.Length);

            if (!string.IsNullOrEmpty(error))
            {
                _logger?.LogWarning("OpenClaw 执行有错误输出: {Error}", error.Length > 200 ? error.Substring(0, 200) + "..." : error);
                if (!string.IsNullOrEmpty(output))
                {
                    output += Environment.NewLine + "--- STDERR ---" + Environment.NewLine + error;
                }
                else
                {
                    output = error;
                }
            }

            if (process.ExitCode == 0)
            {
                _logger?.LogInformation("OpenClaw 成功调用，耗时 {Ms}ms", stopwatch.ElapsedMilliseconds);
                return ("success", output);
            }

            _logger?.LogError("OpenClaw 执行失败，ExitCode: {Code}", process.ExitCode);
            return ("failed", $"ExitCode: {process.ExitCode}" + Environment.NewLine + output);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogError("OpenClaw 调用超时");
            return ("timeout", $"Timeout after {timeoutSeconds} seconds");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "OpenClaw 调用异常");
            var errorMsg = $"{ex.GetType().Name}: {ex.Message}";
            if (ex is System.ComponentModel.Win32Exception win32Ex)
                errorMsg += $", Win32ErrorCode: {win32Ex.NativeErrorCode}";
            if (ex.InnerException != null)
                errorMsg += $", Inner: {ex.InnerException.Message}";
            return ("error", errorMsg);
        }
    }
}