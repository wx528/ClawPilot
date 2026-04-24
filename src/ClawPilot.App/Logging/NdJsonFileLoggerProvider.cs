using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClawPilot.App.Logging;

/// <summary>
/// NDJSON 结构化文件日志提供程序 — 每行一个独立 JSON 对象，便于 jq / 日志分析工具处理
/// </summary>
public class NdJsonFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public NdJsonFileLoggerProvider(string logDirectory, LogLevel minLevel = LogLevel.Debug)
    {
        _logDirectory = logDirectory;
        _minLevel = minLevel;

        if (!Directory.Exists(_logDirectory))
            Directory.CreateDirectory(_logDirectory);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new NdJsonFileLogger(categoryName, _logDirectory, _minLevel, _lock);
    }

    public void Dispose()
    {
        // 不需要释放资源
    }

    private class NdJsonFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _logDirectory;
        private readonly LogLevel _minLevel;
        private readonly object _lock;

        public NdJsonFileLogger(string categoryName, string logDirectory, LogLevel minLevel, object lockObj)
        {
            _categoryName = categoryName;
            _logDirectory = logDirectory;
            _minLevel = minLevel;
            _lock = lockObj;
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

            var entry = new LogEntry
            {
                Timestamp = DateTimeOffset.Now,
                Level = logLevel.ToString(),
                Category = _categoryName,
                Message = message,
                Exception = exception != null ? new ExceptionInfo(exception) : null
            };

            var fileName = $"debug-{DateTime.Now:yyyyMMdd}.ndjson";
            var filePath = Path.Combine(_logDirectory, fileName);

            try
            {
                var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

                lock (_lock)
                {
                    File.AppendAllText(filePath, line, System.Text.Encoding.UTF8);
                }
            }
            catch
            {
                // 日志写入失败时静默忽略，避免日志系统本身导致应用崩溃
            }
        }
    }

    private class LogEntry
    {
        public DateTimeOffset Timestamp { get; set; }
        public string Level { get; set; } = "";
        public string Category { get; set; } = "";
        public string Message { get; set; } = "";
        public ExceptionInfo? Exception { get; set; }
    }

    private class ExceptionInfo
    {
        public string Type { get; set; }
        public string Message { get; set; }
        public string? StackTrace { get; set; }
        public ExceptionInfo? InnerException { get; set; }

        public ExceptionInfo(Exception ex)
        {
            Type = ex.GetType().FullName ?? ex.GetType().Name;
            Message = ex.Message;
            StackTrace = ex.StackTrace;
            if (ex.InnerException != null)
                InnerException = new ExceptionInfo(ex.InnerException);
        }
    }
}
