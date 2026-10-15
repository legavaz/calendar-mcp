using CalendarMcp.Reminders.Notification;

namespace CalendarMcp.Reminders;

/// <summary>
/// Listens for chat commands and answers them with the same layout the digest
/// uses. Runs as a long poll so the process sleeps on the server instead of
/// polling in a tight loop.
/// </summary>
public sealed class TelegramCommandService
{
    private readonly ReminderOptions _options;
    private readonly IMeetingSource _source;
    private readonly ReminderStateStore _state;
    private readonly TelegramClient _client;
    private readonly INotifier _notifier;
    private readonly ILogger<TelegramCommandService> _logger;

    private readonly TimeZoneInfo _zone;

    public TelegramCommandService(
        ReminderOptions options,
        IMeetingSource source,
        ReminderStateStore state,
        TelegramClient client,
        INotifier notifier,
        ILogger<TelegramCommandService> logger)
    {
        _options = options;
        _source = source;
        _state = state;
        _client = client;
        _notifier = notifier;
        _logger = logger;
        _zone = options.ResolveTimeZone();
    }

    /// <summary>
    /// Drains updates until cancelled. The offset advances after each batch, so a
    /// command is answered exactly once even across restarts; Telegram keeps
    /// unacknowledged updates for about a day, which covers a machine that was
    /// off when the command was sent.
    /// </summary>
    public async Task RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var offset = await _state.GetUpdateOffsetAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<TelegramUpdate> updates;
            try
            {
                updates = await _client.GetUpdatesAsync(
                    _options.Telegram.BotToken,
                    offset,
                    _options.Telegram.PollTimeoutSeconds,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (updates.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                continue;
            }

            foreach (var update in updates.OrderBy(u => u.UpdateId))
            {
                await HandleAsync(update, now, cancellationToken);
                offset = update.UpdateId + 1;
            }

            await _state.SetUpdateOffsetAsync(offset, cancellationToken);
        }
    }

    /// <summary>
    /// Answers one update. Messages from any chat other than the configured one are
    /// skipped silently, so the bot stays quiet in groups it was added to.
    /// </summary>
    private async Task HandleAsync(TelegramUpdate update, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!string.Equals(update.ChatId, _options.Telegram.ChatId, StringComparison.Ordinal))
        {
            _logger.LogDebug("Ignoring command from chat {ChatId}", update.ChatId);
            return;
        }

        if (!CommandInterpreter.TryParse(update.Text, out var period))
        {
            await _notifier.SendAsync(CommandInterpreter.HelpText, cancellationToken);
            return;
        }

        foreach (var message in await BuildReplyAsync(period, now, cancellationToken))
        {
            await _notifier.SendAsync(message, cancellationToken);
        }
    }

    /// <summary>
    /// Builds the reply for a period. The calendar is read fresh here because a
    /// command is a direct request for current data and is rare enough that the
    /// extra call does not matter.
    /// </summary>
    private async Task<IReadOnlyList<string>> BuildReplyAsync(MeetingPeriod period, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var local = TimeZoneInfo.ConvertTime(now, _zone);
        var today = DateOnly.FromDateTime(local.DateTime);

        await _source.ResolveAccountAsync(cancellationToken);

        var (start, end) = MeetingSelection.Range(period, today, _options.Digest.WeekDays, _zone);
        var meetings = await _source.GetMeetingsAsync(start, end, cancellationToken);

        var selected = MeetingSelection.ForPeriod(
            meetings, period, local, _zone, _source.OwnEmail, _options.Digest.WeekDays);

        var formatter = new MeetingFormatter(_zone, _options.Digest.WeekDays);
        var messages = formatter.Format(period, selected, today);

        return messages.Count > 0 ? messages : [NoMeetingsText(period, today)];
    }

    /// <summary>Reply for a period with nothing scheduled.</summary>
    public static string NoMeetingsText(MeetingPeriod period, DateOnly today) => period switch
    {
        MeetingPeriod.Tomorrow => $"Встречи завтра, {today.AddDays(1):ddd dd.MM}: пусто",
        MeetingPeriod.Week => "На неделе встреч нет",
        _ => "На сегодня встреч нет"
    };
}