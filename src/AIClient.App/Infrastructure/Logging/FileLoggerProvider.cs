using System.Collections.Concurrent;
using System.IO;
using System.Text;
using AIClient.Application.Configuration;
using Microsoft.Extensions.Logging;

namespace AIClient.App.Infrastructure.Logging;

/// <summary>
/// Writes log entries to a daily rolling file under <c>%APPDATA%\AIClient\logs</c>.
/// </summary>
/// <remarks>
/// A desktop app has no console to read, so a log file is the only way a user can send
/// anything useful when something goes wrong. Writes are queued and drained on a single
/// background thread: logging happens on the dispatcher during UI work and on a worker
/// thread during streaming, and neither may block on file I/O.
///
/// Nothing here redacts anything. Keeping secrets out of the log is the responsibility of
/// the call sites, which never pass a key, an Authorization header or full message content
/// to a logger. A sink that scrubbed after the fact would only make an unsafe call site
/// look safe.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDirectory;
    private readonly LogLevel _minimumLevel;
    private readonly int _retentionDays;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly Thread _writerThread;
    private readonly Lock _fileLock = new();

    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private bool _disposed;

    public FileLoggerProvider(string logsDirectory, LogLevel minimumLevel, int retentionDays)
    {
        _logsDirectory = logsDirectory;
        _minimumLevel = minimumLevel;
        _retentionDays = retentionDays;

        Directory.CreateDirectory(_logsDirectory);
        DeleteExpiredLogs();

        _writerThread = new Thread(DrainQueue)
        {
            IsBackground = true,
            Name = "AIClient.FileLogger",
        };

        _writerThread.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => level >= _minimumLevel && level != LogLevel.None;

    /// <summary>
    /// Hands an entry to the writer thread. Drops it rather than blocking when the queue is
    /// full: a burst of log lines must never stall a streaming response.
    /// </summary>
    internal void Enqueue(string entry)
    {
        if (_disposed)
        {
            return;
        }

        _queue.TryAdd(entry);
    }

    private void DrainQueue()
    {
        foreach (var entry in _queue.GetConsumingEnumerable())
        {
            try
            {
                WriteEntry(entry);
            }
            catch (IOException)
            {
                // A locked or full disk must not take the application down with it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void WriteEntry(string entry)
    {
        lock (_fileLock)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            if (_writer is null || today != _currentDate)
            {
                _writer?.Dispose();

                _currentDate = today;
                var path = Path.Combine(_logsDirectory, $"aiclient-{today:yyyy-MM-dd}.log");

                _writer = new StreamWriter(
                    new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                    Encoding.UTF8)
                {
                    AutoFlush = true,
                };
            }

            _writer.WriteLine(entry);
        }
    }

    /// <summary>Removes logs past the retention window so the folder cannot grow forever.</summary>
    private void DeleteExpiredLogs()
    {
        if (_retentionDays <= 0)
        {
            return;
        }

        try
        {
            var cutoff = DateTime.Now.AddDays(-_retentionDays);

            foreach (var file in Directory.EnumerateFiles(_logsDirectory, "aiclient-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // Housekeeping is best-effort; a failure here is not worth reporting.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        // Bounded wait: a shutdown must not hang on a stuck disk.
        _writerThread.Join(TimeSpan.FromSeconds(2));

        lock (_fileLock)
        {
            _writer?.Dispose();
            _writer = null;
        }

        _queue.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;

            // "AIClient.Infrastructure.Providers.ProviderRegistry" is noise in a log line;
            // the type name alone is what identifies the source.
            var lastDot = category.LastIndexOf('.');
            _category = lastDot >= 0 && lastDot < category.Length - 1
                ? category[(lastDot + 1)..]
                : category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var builder = new StringBuilder(256);

            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(Abbreviate(logLevel)).Append("] ")
                .Append(_category).Append(": ")
                .Append(formatter(state, exception));

            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }

            _provider.Enqueue(builder.ToString());
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}

/// <summary>Registration helper so the host wiring stays a single line.</summary>
public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFileLogger(
        this ILoggingBuilder builder,
        IAppPaths paths,
        StorageSettings storage)
    {
        var level = Enum.TryParse<LogLevel>(storage.MinimumLogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        builder.AddProvider(new FileLoggerProvider(paths.LogsDirectory, level, storage.LogRetentionDays));
        return builder;
    }
}
