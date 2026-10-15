using CalendarMcp.Core.Models;

namespace CalendarMcp.Reminders;

/// <summary>
/// Picks the meetings a request is about and drops the ones that must never
/// produce a notification.
/// </summary>
public static class MeetingSelection
{
    /// <summary>
    /// An all-day event or a cancelled meeting is never announced: one has no
    /// useful lead time and the other no longer happens.
    /// </summary>
    public static bool IsAnnounceable(CalendarEvent meeting) =>
        meeting is { IsCancelled: false, IsAllDay: false };

    /// <summary>
    /// Keeps only meetings the mailbox owner takes part in — as organizer or as
    /// an attendee.
    ///
    /// Some Exchange servers return attendees as empty lists and the organizer as
    /// a legacy X500 distinguished name rather than an SMTP address. There is then
    /// nothing to compare against, and filtering on it would silently discard every
    /// meeting, so a meeting with no comparable address is kept: on a personal
    /// mailbox the calendar already only holds the owner's own events.
    /// </summary>
    /// <param name="meeting">Meeting to test.</param>
    /// <param name="ownEmail">Mailbox address, or null when it cannot be resolved.</param>
    public static bool InvolvesOwner(CalendarEvent meeting, string? ownEmail)
    {
        if (string.IsNullOrWhiteSpace(ownEmail)) return true;

        var comparable = false;

        foreach (var candidate in Addresses(meeting))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Contains(candidate, ownEmail)) return true;

            // Only real addresses count as comparable evidence of exclusion.
            comparable |= LooksLikeEmailAddress(candidate);
        }

        return !comparable;
    }

    private static IEnumerable<string?> Addresses(CalendarEvent meeting)
    {
        yield return meeting.Organizer;
        yield return meeting.OrganizerName;

        foreach (var attendee in meeting.Attendees) yield return attendee;
        foreach (var attendee in meeting.AttendeeDetails ?? []) yield return attendee.Email;
    }

    private static bool Contains(string value, string ownEmail) =>
        value.Contains(ownEmail, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeEmailAddress(string value) =>
        value.Contains('@', StringComparison.Ordinal);

    /// <summary>
    /// Selects meetings for a period. <see cref="MeetingPeriod.Today"/> keeps only
    /// what has not started yet, so a query mid-morning does not replay meetings
    /// that already happened.
    /// </summary>
    /// <param name="meetings">Pool to select from.</param>
    /// <param name="period">Period requested.</param>
    /// <param name="nowLocal">Current local time in the target zone.</param>
    /// <param name="zone">Target time zone.</param>
    /// <param name="ownEmail">Mailbox address used for the owner filter.</param>
    /// <param name="weekDays">Number of days the weekly view spans.</param>
    public static IReadOnlyList<CalendarEvent> ForPeriod(
        IEnumerable<CalendarEvent> meetings,
        MeetingPeriod period,
        DateTimeOffset nowLocal,
        TimeZoneInfo zone,
        string? ownEmail,
        int weekDays = 7)
    {
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var (start, end) = Range(period, today, weekDays, zone);

        return meetings
            .Where(IsAnnounceable)
            .Where(m => InvolvesOwner(m, ownEmail))
            .Where(m => m.Start >= start)
            .Where(m => m.Start < end)
            .Where(m => period != MeetingPeriod.Today || m.Start > nowLocal)
            .OrderBy(m => m.Start)
            .ToList();
    }

    /// <summary>Half-open instant range covering the requested period.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Range(MeetingPeriod period, DateOnly today, int weekDays, TimeZoneInfo zone)
    {
        var startDay = period == MeetingPeriod.Tomorrow ? today.AddDays(1) : today;
        var days = period == MeetingPeriod.Week ? Math.Max(1, weekDays) : 1;
        var start = ToOffset(startDay, TimeOnly.MinValue, zone);
        return (start, start.AddDays(days));
    }

    private static DateTimeOffset ToOffset(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(time), DateTimeKind.Unspecified);
        var offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}

/// <summary>
/// Renders meetings as plain-text messages: one header, then two lines per
/// meeting, with the online link on its own line when there is one.
/// </summary>
public sealed class MeetingFormatter
{
    private const int SubjectWidth = 26;
    private const int NamesWidth = 60;
    private const int MaxNames = 3;
    private const int MessageBudget = 3800;

    private readonly TimeZoneInfo _zone;
    private readonly int _weekDays;

    public MeetingFormatter(TimeZoneInfo zone, int weekDays = 7)
    {
        _zone = zone;
        _weekDays = Math.Max(1, weekDays);
    }

    /// <summary>
    /// Produces one message for a single-day period, or one message per day with
    /// meetings for the weekly view. Empty days yield no message.
    /// </summary>
    /// <param name="period">Period the meetings belong to.</param>
    /// <param name="meetings">Already-selected meetings.</param>
    /// <param name="today">Today's date in the target zone.</param>
    public IReadOnlyList<string> Format(MeetingPeriod period, IReadOnlyList<CalendarEvent> meetings, DateOnly today)
    {
        if (meetings.Count == 0) return [];

        return period == MeetingPeriod.Week
            ? FormatWeek(meetings, today)
            : [FormatSingleDay(period, meetings, today)];
    }

    /// <summary>Header and meetings for today, with the day named explicitly.</summary>
    public string FormatToday(IReadOnlyList<CalendarEvent> meetings, DateOnly today) =>
        FormatSingleDay(MeetingPeriod.Today, meetings, today);

    /// <summary>Header and meetings for tomorrow, naming the weekday so it reads naturally.</summary>
    public string FormatTomorrow(IReadOnlyList<CalendarEvent> meetings, DateOnly today) =>
        FormatSingleDay(MeetingPeriod.Tomorrow, meetings, today);

    /// <summary>
    /// One message per day that has meetings. A day that overflows the message
    /// budget is split again at meeting boundaries with a continuation marker.
    /// </summary>
    public IReadOnlyList<string> FormatWeek(IReadOnlyList<CalendarEvent> meetings, DateOnly today)
    {
        var messages = new List<string>();
        var days = meetings
            .GroupBy(m => DateOnly.FromDateTime(Local(m.Start).Date))
            .OrderBy(g => g.Key)
            .ToList();

        var weekHeader = $"Неделя с {today:dd.MM}";
        var isFirst = true;

        foreach (var day in days)
        {
            var chunks = Split(day.ToList());
            for (var i = 0; i < chunks.Count; i++)
            {
                var header = i == 0 && isFirst ? $"{weekHeader}\n\n{DayHeader(day.Key)}" : DayHeader(day.Key);
                if (i > 0) header = $"{header} (продолжение)";
                messages.Add(header + "\n\n" + chunks[i]);
                isFirst = false;
            }
        }

        return messages;
    }

    /// <summary>Renders a single meeting as two or three lines.</summary>
    public string FormatMeeting(CalendarEvent meeting)
    {
        var start = Local(meeting.Start);
        var end = Local(meeting.End);
        var head = $"{start:HH:mm}–{end:HH:mm}".PadRight(13) + Truncate(meeting.Subject, SubjectWidth);

        var lines = new List<string> { head };

        var people = DescribePeople(meeting);
        if (people.Length > 0) lines.Add($"   с кем: {people}");

        var link = meeting.OnlineMeetingUrl;
        if (!string.IsNullOrWhiteSpace(link)) lines.Add($"   zoom: {link}");

        return string.Join("\n", lines);
    }

    /// <summary>Truncates with an ellipsis, never cutting mid-escape or overflowing.</summary>
    public static string Truncate(string? value, int width)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= width ? trimmed : trimmed[..Math.Max(1, width - 1)] + "…";
    }

    private string FormatSingleDay(MeetingPeriod period, IReadOnlyList<CalendarEvent> meetings, DateOnly today)
    {
        var header = period == MeetingPeriod.Tomorrow
            ? $"Встречи завтра, {FormatDayLabel(today.AddDays(1))}"
            : $"Сводка на {FormatDayLabel(today)}";

        return header + "\n\n" + string.Join("\n\n", meetings.Select(FormatMeeting));
    }

    private string DayHeader(DateOnly day) => FormatDayLabel(day);

    /// <summary>
    /// Weekday and date as "чт 15.10". The two parts are formatted separately so
    /// the dot inside the date survives stripping the trailing dot of the
    /// abbreviated weekday.
    /// </summary>
    private static string FormatDayLabel(DateOnly day) => $"{day:ddd} {day:dd.MM}".TrimEnd('.');

    private List<string> Split(IReadOnlyList<CalendarEvent> meetings)
    {
        var chunks = new List<string>();
        var current = new List<string>();

        foreach (var meeting in meetings)
        {
            var rendered = FormatMeeting(meeting);
            var projected = current.Count == 0
                ? rendered.Length
                : current.Sum(c => c.Length + 2) + rendered.Length + 2;

            if (current.Count > 0 && projected > MessageBudget)
            {
                chunks.Add(string.Join("\n\n", current));
                current = [rendered];
            }
            else
            {
                current.Add(rendered);
            }
        }

        if (current.Count > 0) chunks.Add(string.Join("\n\n", current));
        return chunks;
    }

    private static string DescribePeople(CalendarEvent meeting)
    {
        var names = new List<string>();

        if (!string.IsNullOrWhiteSpace(meeting.OrganizerName)) names.Add(meeting.OrganizerName);
        foreach (var attendee in meeting.AttendeeDetails ?? [])
        {
            if (string.IsNullOrWhiteSpace(attendee.Name)) continue;
            if (names.Any(n => string.Equals(n, attendee.Name, StringComparison.OrdinalIgnoreCase))) continue;
            names.Add(attendee.Name);
        }

        if (names.Count == 0) return string.Empty;

        var shown = names.Take(MaxNames).Select(n => Truncate(n, 22));
        var text = string.Join(", ", shown);
        if (names.Count > MaxNames) text += $", ещё {names.Count - MaxNames}";
        return Truncate(text, NamesWidth);
    }

    private DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _zone);
}