using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarMcp.Reminders;

/// <summary>
/// Persistent reminder bookkeeping. Without it every process start would replay
/// reminders that already went out, so the file is what makes restarts safe.
/// </summary>
public sealed class ReminderStateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<ReminderStateStore> _logger;

    private State _state = new();

    private sealed class State
    {
        [JsonPropertyName("sentReminders")]
        public Dictionary<string, string> SentReminders { get; set; } = new();

        [JsonPropertyName("digestDates")]
        public List<string> DigestDates { get; set; } = [];

        [JsonPropertyName("updateOffset")]
        public int UpdateOffset { get; set; }
    }

    public ReminderStateStore(string path, ILogger<ReminderStateStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Loads the file, treating a missing or corrupt file as empty state.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
            {
                _state = new State();
                return;
            }

            var json = await File.ReadAllTextAsync(_path, cancellationToken);
            _state = JsonSerializer.Deserialize<State>(json) ?? new State();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Reminder state at {Path} is unreadable; starting from empty state", _path);
            _state = new State();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when this meeting was already announced for this lead time.</summary>
    public async Task<bool> IsReminderSentAsync(string eventId, int leadMinutes, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.SentReminders.ContainsKey(Key(eventId, leadMinutes));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records a reminder as sent. Returns false when it was already recorded,
    /// which keeps a retry loop from sending the same alert twice.
    /// </summary>
    public async Task<bool> TryMarkReminderSentAsync(string eventId, int leadMinutes, DateTimeOffset sentAt, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = Key(eventId, leadMinutes);
            if (_state.SentReminders.ContainsKey(key)) return false;

            _state.SentReminders[key] = sentAt.UtcDateTime.ToString("O");
            await SaveAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when the digest for this date already went out.</summary>
    public async Task<bool> IsDigestSentAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.DigestDates.Contains(Text(date));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records that the digest for a date has been sent.</summary>
    public async Task MarkDigestSentAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = Text(date);
            if (_state.DigestDates.Contains(key)) return;

            _state.DigestDates.Add(key);
            await SaveAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Last processed Telegram update id; zero when nothing was processed yet.</summary>
    public async Task<int> GetUpdateOffsetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _state.UpdateOffset;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stores the highest update id seen. Telegram keeps unacknowledged updates
    /// queued for about a day, so an offset that never advances makes the bot
    /// answer the same command over and over.
    /// </summary>
    public async Task SetUpdateOffsetAsync(int offset, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (offset <= _state.UpdateOffset) return;

            _state.UpdateOffset = offset;
            await SaveAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the file, so a fresh start replays everything.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _state = new State();
            if (File.Exists(_path)) File.Delete(_path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Key(string eventId, int leadMinutes) => $"{eventId}|{leadMinutes}";

    private static string Text(DateOnly date) => date.ToString("yyyy-MM-dd");

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });

        // Write to a sibling then move, so a crash mid-write cannot leave a
        // truncated file that would reset dedup state and resend everything.
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, _path, overwrite: true);
    }
}