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
    private readonly string _directory;
    private readonly string _prefix;
    private readonly LogLevel _minimumLevel;
    private readonly Lock _gate = new();

    /// <param name="directory">Directory that receives one file per day.</param>
    /// <param name="prefix">File name prefix, e.g. <c>reminders</c>.</param>
    /// <param name="minimumLevel">Lowest level written.</param>
    public FileLoggerProvider(string directory, string prefix, LogLevel minimumLevel)
    {
        _directory = directory;
        _prefix = prefix;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// File for a given day. The name is resolved per write rather than once at
    /// startup: a host that runs for weeks would otherwise keep appending to the
    /// file from the day it started, hiding recent activity from anyone looking
    /// for today's file.
    /// </summary>
    public string PathFor(DateOnly day) =>
        Path.Combine(_directory, $"{_prefix}-{day:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            File.AppendAllText(PathFor(DateOnly.FromDateTime(DateTime.Now)), line + Environment.NewLine, Encoding.UTF8);
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