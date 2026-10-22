using CalendarMcp.Core.Models;
using CalendarMcp.Reminders.Notification;

namespace CalendarMcp.Reminders;

/// <summary>
/// Drives the digest and the pre-meeting alerts. One pass does everything due at
/// a given instant, so the same code serves the long-running loop and --once.
/// </summary>
public sealed class ReminderService
{
    private readonly ReminderOptions _options;
    private readonly IMeetingSource _source;
    private readonly MeetingFormatter _formatter;
    private readonly INotifier _notifier;
    private readonly ReminderStateStore _state;
    private readonly ILogger<ReminderService> _logger;
    private readonly TimeZoneInfo _zone;

    private IReadOnlyList<CalendarEvent> _cache = [];
    private DateTimeOffset _cacheFetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _cacheWindowStart = DateTimeOffset.MinValue;
    private bool _cacheLoaded;

    private DateTimeOffset _lastHeartbeatAt = DateTimeOffset.MinValue;
    private int _completedPasses;

    // Silence is indistinguishable from a stuck host, so the digest decision is
    // logged once per day and reason instead of on every tick.
    private DateOnly? _digestLoggedFor;
    private string? _digestLoggedReason;

    public ReminderService(
        ReminderOptions options,
        IMeetingSource source,
        MeetingFormatter formatter,
        INotifier notifier,
        ReminderStateStore state,
        ILogger<ReminderService> logger)
    {
        _options = options;
        _source = source;
        _formatter = formatter;
        _notifier = notifier;
        _state = state;
        _logger = logger;
        _zone = options.ResolveTimeZone();
    }

    /// <summary>Run once: a single evaluation pass, used by --once and by tests.</summary>
    public Task<int> RunPassAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        RunPassAsync(now, dryRun: false, cancellationToken);

    /// <summary>
    /// Evaluates one instant and sends whatever is due. The calendar is refetched
    /// at most once per <see cref="ReminderOptions.RefreshMinutes"/>; the tick in
    /// between reuses the cache, so a 30-second loop does not mean 30 EWS calls a
    /// minute.
    /// </summary>
    /// <param name="now">Current instant.</param>
    /// <param name="dryRun">Log what would be sent without sending it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of messages sent.</returns>
    public async Task<int> RunPassAsync(DateTimeOffset now, bool dryRun, CancellationToken cancellationToken = default)
    {
        await EnsureCacheAsync(now, cancellationToken);

        var sent = 0;
        sent += await TrySendDigestAsync(now, dryRun, cancellationToken);
        sent += await TrySendRemindersAsync(now, dryRun, cancellationToken);

        _completedPasses++;
        await LogHeartbeatAsync(now, cancellationToken);
        return sent;
    }

    /// <summary>
    /// Writes one line an hour proving the loop is still turning. Without it an
    /// empty calendar and a wedged process look exactly the same in the log.
    /// </summary>
    private async Task LogHeartbeatAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now - _lastHeartbeatAt < TimeSpan.FromHours(1)) return;

        _lastHeartbeatAt = now;
        _logger.LogInformation(
            "Reminder host alive: {Passes} passes, {Meetings} meetings cached, digest today: {Digest}",
            _completedPasses, _cache.Count, await DescribeDigestStateAsync(now, cancellationToken));
    }

    /// <summary>
    /// Short status of today's digest, used by the heartbeat line. The state file is
    /// consulted rather than the in-memory flag, so a restart does not report
    /// "pending" for a day whose digest already went out.
    /// </summary>
    private async Task<string> DescribeDigestStateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_options.Digest.Enabled) return "disabled";

        var local = TimeZoneInfo.ConvertTime(now, _zone);
        if (local.TimeOfDay > _options.Digest.Cutoff) return "window closed";
        if (local.TimeOfDay < _options.Digest.Time) return "scheduled";

        var today = DateOnly.FromDateTime(local.DateTime);
        return await _state.IsDigestSentAsync(today, cancellationToken) ? "sent" : "pending";
    }

    /// <summary>Refreshes the cached meeting list when it has gone stale.</summary>
    public async Task EnsureCacheAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.RefreshMinutes));

        // The window starts at local midnight so the digest sees meetings that
        // already started but are still running, and extends far enough for the
        // weekly view.
        var localNow = TimeZoneInfo.ConvertTime(now, _zone);
        var startLocal = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
        var from = new DateTimeOffset(startLocal, _zone.GetUtcOffset(startLocal));
        var until = from.AddDays(Math.Max(1, _options.Digest.WeekDays));

        // A stale-by-interval cache is refetched, and so is one anchored to a
        // previous day: a long-running host must roll its window over at midnight
        // instead of serving the first day's meetings forever.
        //
        // Staleness is tracked by a flag, not by the cache being empty. A day
        // with no meetings must not turn every tick into another Exchange call.
        var stale = !_cacheLoaded
            || now - _cacheFetchedAt >= interval
            || _cacheWindowStart != from;

        if (!stale) return;

        _cache = await _source.GetMeetingsAsync(from, until, cancellationToken);
        _cacheFetchedAt = now;
        _cacheWindowStart = from;
        _cacheLoaded = true;
        _logger.LogDebug("Meeting cache refreshed: {Count} meetings", _cache.Count);
    }

    /// <summary>Meetings currently cached; exposed for command replies.</summary>
    public IReadOnlyList<CalendarEvent> CachedMeetings => _cache;

    /// <summary>
    /// Sends the morning digest once per day, after <see cref="DigestOptions.Time"/>
    /// and before <see cref="DigestOptions.Cutoff"/>. A day with no meetings still
    /// gets a short "nothing today" message: silence would look like a broken host.
    /// </summary>
    private async Task<int> TrySendDigestAsync(DateTimeOffset now, bool dryRun, CancellationToken cancellationToken)
    {
        if (!_options.Digest.Enabled) return 0;

        var local = TimeZoneInfo.ConvertTime(now, _zone);
        var today = DateOnly.FromDateTime(local.DateTime);

        if (local.TimeOfDay < _options.Digest.Time) return 0;
        if (local.TimeOfDay > _options.Digest.Cutoff)
        {
            LogDigestDecision(today, "window closed");
            return 0;
        }

        if (await _state.IsDigestSentAsync(today, cancellationToken)) return 0;

        var meetings = MeetingSelection.ForPeriod(
            _cache, MeetingPeriod.Today, local, _zone, _source.OwnEmail);

        // A day without meetings still produces a message. Silence would be
        // indistinguishable from a host that stopped working, which is exactly how
        // an empty day used to be reported as a broken bot.
        var message = meetings.Count == 0
            ? TelegramCommandService.NoMeetingsText(MeetingPeriod.Today, today)
            : _formatter.FormatToday(meetings, today);

        if (dryRun)
        {
            _logger.LogInformation("[dry-run] digest for {Date}:\n{Message}", today, message);
        }
        else
        {
            await _notifier.SendAsync(message, cancellationToken);
            await _state.MarkDigestSentAsync(today, cancellationToken);
            _digestLoggedFor = today;
            _digestLoggedReason = "sent";
            _logger.LogInformation("Sent morning digest for {Date} ({Count} meetings)", today, meetings.Count);
        }

        return 1;
    }

    /// <summary>
    /// Records why the digest was not sent, once per day and reason. Previously an
    /// empty day produced no log line at all, which reads exactly like a host that
    /// stopped working.
    /// </summary>
    private void LogDigestDecision(DateOnly today, string reason)
    {
        if (_digestLoggedFor == today && _digestLoggedReason == reason) return;

        _digestLoggedFor = today;
        _digestLoggedReason = reason;
        _logger.LogInformation("Digest for {Date} not sent: {Reason}", today, reason);
    }

    /// <summary>
    /// Sends an alert for every meeting whose lead time falls inside the tick.
    /// The window has slack on both edges so a tick that lands slightly late still
    /// fires; a meeting whose whole window has passed is skipped rather than sent
    /// late, which keeps a restarted host from dumping a backlog of alerts.
    /// </summary>
    private async Task<int> TrySendRemindersAsync(DateTimeOffset now, bool dryRun, CancellationToken cancellationToken)
    {
        var local = TimeZoneInfo.ConvertTime(now, _zone);
        var tolerance = Math.Max(0, _options.LeadToleranceMinutes);
        var sent = 0;

        foreach (var meeting in _cache)
        {
            if (!MeetingSelection.IsAnnounceable(meeting)) continue;
            if (!MeetingSelection.InvolvesOwner(meeting, _source.OwnEmail)) continue;

            foreach (var lead in _options.LeadMinutes)
            {
                if (lead <= 0) continue;

                var minutesUntil = (meeting.Start - now).TotalMinutes;
                if (minutesUntil > lead + tolerance) continue;
                if (minutesUntil <= lead - tolerance) continue;

                if (!dryRun && await _state.IsReminderSentAsync(meeting.Id, lead, cancellationToken)) continue;

                var message = FormatReminder(meeting, lead, _zone);
                if (dryRun)
                {
                    _logger.LogInformation("[dry-run] reminder for {Subject}:\n{Message}", meeting.Subject, message);
                    sent++;
                    continue;
                }

                var recorded = await _state.TryMarkReminderSentAsync(meeting.Id, lead, now, cancellationToken);
                if (!recorded) continue;

                await _notifier.SendAsync(message, cancellationToken);
                sent++;
                _logger.LogInformation("Sent {Lead}-minute reminder for {Subject}", lead, meeting.Subject);
            }
        }

        return sent;
    }

    /// <summary>Renders one pre-meeting alert: the countdown headline, then the meeting block.</summary>
    public static string FormatReminder(CalendarEvent meeting, int leadMinutes, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(meeting.Start, zone);
        var headline = leadMinutes >= 60 && leadMinutes % 60 == 0
            ? $"Через {leadMinutes / 60} ч"
            : $"Через {leadMinutes} мин";

        var formatter = new MeetingFormatter(zone);
        return $"{headline}\n\n{formatter.FormatMeeting(meeting)}";
    }
}