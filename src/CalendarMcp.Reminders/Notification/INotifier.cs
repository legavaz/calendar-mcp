namespace CalendarMcp.Reminders.Notification;

/// <summary>
/// Destination for reminder text. One chat is served, so the interface stays a
/// single send; a second channel can be added later without touching callers.
/// </summary>
public interface INotifier
{
    /// <summary>Sends one plain-text message. Implementations must not throw on delivery failure.</summary>
    Task SendAsync(string message, CancellationToken cancellationToken = default);
}

/// <summary>Records messages instead of sending them, for dry runs and tests.</summary>
public sealed class NullNotifier : INotifier
{
    private readonly List<string> _messages = [];

    /// <summary>Messages captured so far, in send order.</summary>
    public IReadOnlyList<string> Messages => _messages;

    public Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        lock (_messages) _messages.Add(message);
        return Task.CompletedTask;
    }
}