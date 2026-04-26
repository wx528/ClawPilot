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
    private readonly int _maxFileSizeBytes;
    private readonly int _archiveAfterDays;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public NdJsonFileLoggerProvider(string logDirectory, LogLevel minLevel = LogLevel.Debug, int maxFileSizeBytes = 10485760, int archiveAfterDays = 7)
    {
        _logDirectory = logDirectory;
        _minLevel = minLevel;
        _maxFileSizeBytes = maxFileSizeBytes;
        _archiveAfterDays = archiveAfterDays;

        if (!Directory.Exists(_logDirectory))
            Directory.CreateDirectory(_logDirectory);

        _ = Task.Run(ArchiveOldLogsAsync);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new NdJsonFileLogger(categoryName, _logDirectory, _minLevel, _lock, _maxFileSizeBytes);
    }

    public void Dispose()
    {
        // 不需要释放资源
    }

    private async Task ArchiveOldLogsAsync()
    {
        try
        {
            var files = Directory.GetFiles(_logDirectory, "debug-*.ndjson");
            var cutoff = DateTime.Now.AddDays(-_archiveAfterDays);

            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                // 解析日期: debug-YYYYMMDD 或 debug-YYYYMMDD.N
                var datePart = fileName.Split('.')[0];
                if (datePart.Length < 12) continue;

                var dateStr = datePart.Substring(6);
                if (!DateTime.TryParseExact(dateStr, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var fileDate))
                    continue;

                if (fileDate >= cutoff)
                    continue;

                var gzPath = file + ".gz";
                using (var src = File.OpenRead(file))
                using (var dst = File.Create(gzPath))
                using (var gz = new System.IO.Compression.GZipStream(dst, System.IO.Compression.CompressionLevel.Optimal))
                {
                    await src.CopyToAsync(gz);
                }

                File.Delete(file);
            }
        }
        catch
        {
            // 归档失败时静默忽略，避免日志系统自身崩溃
        }
    }

    private class NdJsonFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _logDirectory;
        private readonly LogLevel _minLevel;
        private readonly object _lock;
        private readonly int _maxFileSizeBytes;

        public NdJsonFileLogger(string categoryName, string logDirectory, LogLevel minLevel, object lockObj, int maxFileSizeBytes)
        {
            _categoryName = categoryName;
            _logDirectory = logDirectory;
            _minLevel = minLevel;
            _lock = lockObj;
            _maxFileSizeBytes = maxFileSizeBytes;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        private string ResolveLogFilePath()
        {
            var date = DateTime.Now.ToString("yyyyMMdd");
            var basePath = Path.Combine(_logDirectory, $"debug-{date}.ndjson");

            if (!File.Exists(basePath))
                return basePath;

            var info = new FileInfo(basePath);
            if (info.Length < _maxFileSizeBytes)
                return basePath;

            int index = 1;
            while (true)
            {
                var shardPath = Path.Combine(_logDirectory, $"debug-{date}.{index}.ndjson");
                if (!File.Exists(shardPath))
                    return shardPath;

                var shardInfo = new FileInfo(shardPath);
                if (shardInfo.Length < _maxFileSizeBytes)
                    return shardPath;

                index++;
            }
        }

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

            var filePath = ResolveLogFilePath();

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
