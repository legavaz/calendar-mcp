using System.Text;
using Microsoft.Extensions.Logging;

namespace CalendarMcp.Reminders.Logging;

/// <summary>
/// Minimal file logger. The host is a background service whose console is either
/// hidden or closed, so the log file is the only place a failure can be read
/// from. A purpose-built provider keeps this dependency-free and predictable,
/// which suits a few hundred lines a day.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly LogLevel _minimumLevel;
    private readonly Lock _gate = new();

    public FileLoggerProvider(string path, LogLevel minimumLevel)
    {
        _path = path;
        _minimumLevel = minimumLevel;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private bool IsEnabled(LogLevel level) => level >= _minimumLevel && level != LogLevel.None;

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {category}: {message}";

            if (exception is not null) line += Environment.NewLine + exception;

            try
            {
                provider.Write(line);
            }
            catch (Exception)
            {
                // A logging failure must never take the host down; the console
                // provider still has the original message.
            }
        }
    }
}