using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;

namespace ClawPilot.App.Logging;

public class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logFilePath;
    private readonly LogLevel _minLevel;

    public FileLoggerProvider(string logFilePath, LogLevel minLevel = LogLevel.Information)
    {
        _logFilePath = logFilePath;
        _minLevel = minLevel;
        // 确保日志目录存在
        var dir = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, _logFilePath, _minLevel);
    }

    public void Dispose()
    {
        // 不需要释放资源
    }

    private class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _logFilePath;
        private readonly LogLevel _minLevel;
        private readonly object _lock = new();

        public FileLogger(string categoryName, string logFilePath, LogLevel minLevel)
        {
            _categoryName = categoryName;
            _logFilePath = logFilePath;
            _minLevel = minLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception == null)
                return;

            var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_categoryName}: {message}";
            if (exception != null)
                logEntry += Environment.NewLine + exception;

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // 如果写文件失败，忽略
                }
            }
        }
    }
}