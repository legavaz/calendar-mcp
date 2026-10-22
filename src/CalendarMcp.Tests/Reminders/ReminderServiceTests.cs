using CalendarMcp.Core.Models;
using CalendarMcp.Reminders;
using CalendarMcp.Reminders.Notification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalendarMcp.Tests.Reminders;

/// <summary>
/// Covers the daily digest and the pre-meeting alerts, including the windows and
/// the rules that stop a duplicate or stale message.
/// </summary>
[TestClass]
public class ReminderServiceTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private sealed class FakeSource(List<CalendarEvent> meetings, string? ownEmail = null) : IMeetingSource
    {
        public string? OwnEmail { get; } = ownEmail;
        public int Calls { get; private set; }

        public Task<AccountInfo?> ResolveAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountInfo?>(new AccountInfo
            {
                Id = "acct",
                DisplayName = "Account",
                Provider = "exchange"
            });

        public Task<IReadOnlyList<CalendarEvent>> GetMeetingsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<CalendarEvent>>(meetings);
        }
    }

    private static ReminderOptions Options() => new()
    {
        Enabled = true,
        AccountId = "acct",
        Telegram = new TelegramOptions { Enabled = true, BotToken = "t", ChatId = "1" },
        TimeZone = "Europe/Moscow",
        Digest = new DigestOptions { Enabled = true, Time = new TimeSpan(8, 30, 0), Cutoff = new TimeSpan(12, 0, 0) }
    };

    private static DateTimeOffset At(int day, int hour, int minute = 0)
    {
        var local = DateTime.SpecifyKind(new DateTime(2026, 10, day, hour, minute, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Moscow.GetUtcOffset(local));
    }

    private static CalendarEvent Meeting(string id, DateTimeOffset start, DateTimeOffset end, bool allDay = false, bool cancelled = false)
    {
        var meeting = new CalendarEvent
        {
            Id = id,
            AccountId = "acct",
            CalendarId = "calendar",
            Subject = $"Встреча {id}",
            Start = start,
            End = end,
            IsAllDay = allDay,
            IsCancelled = cancelled
        };

        meeting.AttendeeDetails.Add(new EventAttendee { Email = "me@example.com", Name = "Я", Type = "required" });
        return meeting;
    }

    private static (ReminderService Service, NullNotifierNotifier Notifier, ReminderStateStore Store) Create(
        IReadOnlyList<CalendarEvent> meetings,
        ReminderOptions? options = null,
        string? ownEmail = "me@example.com",
        string? statePath = null)
    {
        options ??= Options();
        var path = statePath ?? Path.Combine(Path.GetTempPath(), "calendar-mcp-tests", Guid.NewGuid().ToString("N"), "state.json");
        var store = new ReminderStateStore(path, NullLogger<ReminderStateStore>.Instance);
        var notifier = new NullNotifierNotifier();
        var formatter = new MeetingFormatter(Moscow, options.Digest.WeekDays);

        var service = new ReminderService(
            options,
            new FakeSource(meetings.ToList(), ownEmail),
            formatter,
            notifier,
            store,
            NullLogger<ReminderService>.Instance);

        return (service, notifier, store);
    }

    private sealed class NullNotifierNotifier : INotifier
    {
        public List<string> Sent { get; } = [];

        public Task SendAsync(string message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task Digest_BeforeScheduledTime_IsNotSent()
    {
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))]);
        await store.LoadAsync();

        var sent = await service.RunPassAsync(At(15, 8, 0).ToUniversalTime());

        Assert.AreEqual(0, sent);
        Assert.AreEqual(0, notifier.Sent.Count);
    }

    [TestMethod]
    public async Task Digest_AfterScheduledTime_SendsAndMarksTheDay()
    {
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))]);
        await store.LoadAsync();

        var sent = await service.RunPassAsync(At(15, 8, 31).ToUniversalTime());

        Assert.AreEqual(1, sent);
        StringAssert.StartsWith(notifier.Sent[0], "Сводка на");
        Assert.IsTrue(await store.IsDigestSentAsync(new DateOnly(2026, 10, 15)));

        var again = await service.RunPassAsync(At(15, 9, 0).ToUniversalTime());
        Assert.AreEqual(0, again, "the digest must not repeat on the same day");
        Assert.AreEqual(1, notifier.Sent.Count);
    }

    [TestMethod]
    public async Task Digest_AfterCutoff_IsSkipped()
    {
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))]);
        await store.LoadAsync();

        var sent = await service.RunPassAsync(At(15, 13, 0).ToUniversalTime());

        Assert.AreEqual(0, sent);
        Assert.AreEqual(0, notifier.Sent.Count);
    }

    [TestMethod]
    public async Task Digest_EmptyDay_StillSendsAMessageAndMarksTheDay()
    {
        var (service, notifier, store) = Create([]);
        await store.LoadAsync();

        var sent = await service.RunPassAsync(At(15, 9, 0).ToUniversalTime());

        Assert.AreEqual(1, sent, "an empty day still confirms itself, so silence means trouble");
        Assert.AreEqual(1, notifier.Sent.Count);
        StringAssert.Contains(notifier.Sent[0], "встреч нет");
        Assert.IsTrue(await store.IsDigestSentAsync(new DateOnly(2026, 10, 15)));

        var again = await service.RunPassAsync(At(15, 10, 0).ToUniversalTime());
        Assert.AreEqual(0, again, "the empty digest must not repeat");
    }

    [TestMethod]
    public async Task Digest_OnlyOnce_ForEachNewDay()
    {
        var path = Path.Combine(Path.GetTempPath(), "calendar-mcp-tests", Guid.NewGuid().ToString("N"), "state.json");
        var (service, notifier, store) = Create(
            [Meeting("a", At(15, 16), At(15, 17)), Meeting("b", At(16, 16), At(16, 17))],
            statePath: path);
        await store.LoadAsync();

        await service.RunPassAsync(At(15, 9, 0).ToUniversalTime());
        await service.RunPassAsync(At(16, 9, 0).ToUniversalTime());

        Assert.AreEqual(2, notifier.Sent.Count, "each day gets its own digest");
    }

    [TestMethod]
    public async Task Reminder_SentWhenTheLeadWindowOpens()
    {
        var options = Options();
        options.LeadMinutes = [15];
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))], options);
        await store.LoadAsync();

        var sent = await service.RunPassAsync(At(15, 15, 45).ToUniversalTime());

        Assert.AreEqual(1, sent);
        StringAssert.StartsWith(notifier.Sent[0], "Через 15 мин");
        StringAssert.Contains(notifier.Sent[0], "16:00–17:00");
    }

    [TestMethod]
    public async Task Reminder_NotSentOutsideTheWindow()
    {
        var options = Options();
        options.LeadMinutes = [15];
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))], options);
        await store.LoadAsync();

        var sent = await service.RunPassAsync(At(15, 14, 0).ToUniversalTime());

        Assert.AreEqual(0, sent);
    }

    [TestMethod]
    public async Task Reminder_ToleranceCatchesATickThatLandsSlightlyLate()
    {
        var options = Options();
        options.LeadMinutes = [15];
        options.LeadToleranceMinutes = 2;
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))], options);
        await store.LoadAsync();

        var sent = await service.RunPassAsync(At(15, 15, 46).ToUniversalTime());

        Assert.AreEqual(1, sent, "a tick one minute past the lead time still fires");
    }

    [TestMethod]
    public async Task Reminder_NotRepeatedOnTheNextPass()
    {
        var options = Options();
        options.LeadMinutes = [15];
        options.TickSeconds = 5;
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))], options);
        await store.LoadAsync();

        await service.RunPassAsync(At(15, 15, 45).ToUniversalTime());
        var again = await service.RunPassAsync(At(15, 15, 46).ToUniversalTime());

        Assert.AreEqual(0, again);
        Assert.AreEqual(1, notifier.Sent.Count);
    }

    [TestMethod]
    public async Task Reminder_SkippedForAllDayCancelledAndForeignMeetings()
    {
        var options = Options();
        options.LeadMinutes = [15];

        var foreign = Meeting("foreign", At(15, 16), At(15, 17));
        foreign.AttendeeDetails.Clear();
        foreign.AttendeeDetails.Add(new EventAttendee { Email = "other@example.com", Name = "Кто-то", Type = "required" });

        var meetings = new[]
        {
            Meeting("allday", At(15, 16), At(15, 17), allDay: true),
            Meeting("cancelled", At(15, 16), At(15, 17), cancelled: true),
            foreign
        };

        var (service, notifier, store) = Create(meetings, options);
        await store.LoadAsync();

        var sent = await service.RunPassAsync(At(15, 15, 45).ToUniversalTime());

        Assert.AreEqual(0, sent);
    }

    [TestMethod]
    public async Task Reminder_SentOncePerLeadTime()
    {
        var options = Options();
        options.LeadMinutes = [60, 15];
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))], options);
        await store.LoadAsync();

        await service.RunPassAsync(At(15, 15, 0).ToUniversalTime());
        var countAfterHour = notifier.Sent.Count;

        await service.RunPassAsync(At(15, 15, 45).ToUniversalTime());

        Assert.AreEqual(2, notifier.Sent.Count, "the 60-minute and 15-minute alerts are separate");
        Assert.IsTrue(countAfterHour >= 1);
    }

    [TestMethod]
    public async Task Reminder_HourlyLeadUsesHourWording()
    {
        var options = Options();
        options.LeadMinutes = [60];
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))], options);
        await store.LoadAsync();

        await service.RunPassAsync(At(15, 15, 0).ToUniversalTime());

        StringAssert.StartsWith(notifier.Sent[0], "Через 1 ч");
    }

    [TestMethod]
    public async Task DryRun_LogsWithoutSending()
    {
        var options = Options();
        options.LeadMinutes = [15];
        var (service, notifier, store) = Create([Meeting("a", At(15, 16), At(15, 17))], options);
        await store.LoadAsync();

        var sent = await service.RunPassAsync(At(15, 15, 45).ToUniversalTime(), dryRun: true);

        Assert.AreEqual(0, notifier.Sent.Count);
        Assert.IsFalse(await store.IsReminderSentAsync("a", 15), "a dry run must not consume the reminder");
    }

    [TestMethod]
    public async Task CalendarIsReadOncePerRefreshWindow()
    {
        var source = new CountingSource([Meeting("a", At(15, 16), At(15, 17))]);
        var options = Options();
        options.LeadMinutes = [];
        options.RefreshMinutes = 5;
        options.Digest.Enabled = false;

        var store = new ReminderStateStore(Path.Combine(Path.GetTempPath(), "calendar-mcp-tests", Guid.NewGuid().ToString("N"), "s.json"), NullLogger<ReminderStateStore>.Instance);
        await store.LoadAsync();

        var service = new ReminderService(
            options,
            source,
            new MeetingFormatter(Moscow),
            new NullNotifierNotifier(),
            store,
            NullLogger<ReminderService>.Instance);

        var start = At(15, 12, 0).ToUniversalTime();
        await service.RunPassAsync(start);
        await service.RunPassAsync(start.AddSeconds(30));

        Assert.AreEqual(1, source.Calls, "ticks inside the refresh window reuse the cache");
    }

    [TestMethod]
    public async Task EmptyCalendar_DoesNotRefetchOnEveryTick()
    {
        // An empty day used to look like "never loaded", which turned the loop into
        // an Exchange request every tick.
        var source = new CountingSource([]);
        var options = Options();
        options.LeadMinutes = [15];
        options.RefreshMinutes = 5;
        options.Digest.Enabled = false;

        var store = new ReminderStateStore(Path.Combine(Path.GetTempPath(), "calendar-mcp-tests", Guid.NewGuid().ToString("N"), "s.json"), NullLogger<ReminderStateStore>.Instance);
        await store.LoadAsync();

        var service = new ReminderService(
            options,
            source,
            new MeetingFormatter(Moscow),
            new NullNotifierNotifier(),
            store,
            NullLogger<ReminderService>.Instance);

        var start = At(15, 12, 0).ToUniversalTime();
        for (var i = 0; i < 5; i++)
        {
            await service.RunPassAsync(start.AddSeconds(i * 30));
        }

        Assert.AreEqual(1, source.Calls, "an empty calendar must still be cached");
    }

    private sealed class CountingSource(IReadOnlyList<CalendarEvent> meetings) : IMeetingSource
    {
        public string? OwnEmail => "me@example.com";
        public int Calls { get; private set; }

        public Task<AccountInfo?> ResolveAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountInfo?>(new AccountInfo { Id = "a", DisplayName = "a", Provider = "exchange" });

        public Task<IReadOnlyList<CalendarEvent>> GetMeetingsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(meetings);
        }
    }
}