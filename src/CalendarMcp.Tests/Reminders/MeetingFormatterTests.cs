using CalendarMcp.Core.Models;
using CalendarMcp.Reminders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalendarMcp.Tests.Reminders;

/// <summary>
/// Covers the message layout: two lines per meeting, an optional link line, and
/// one message per day for the weekly view.
/// </summary>
[TestClass]
public class MeetingFormatterTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    private static DateTimeOffset At(int day, int hour, int minute = 0)
    {
        var local = DateTime.SpecifyKind(new DateTime(2026, 10, day, hour, minute, 0), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Moscow.GetUtcOffset(local));
    }

    private static CalendarEvent Meeting(
        string id,
        DateTimeOffset start,
        DateTimeOffset end,
        string subject = "Встреча",
        string? onlineUrl = null)
    {
        var meeting = new CalendarEvent
        {
            Id = id,
            AccountId = "acct",
            CalendarId = "calendar",
            Subject = subject,
            Start = start,
            End = end,
            OnlineMeetingUrl = onlineUrl
        };

        meeting.AttendeeDetails.Add(new EventAttendee { Email = "boss@example.com", Name = "Иванов", Type = "required" });
        meeting.AttendeeDetails.Add(new EventAttendee { Email = "me@example.com", Name = "Лукьянова", Type = "required" });
        return meeting;
    }

    [TestMethod]
    public void FormatMeeting_UsesLocalTimeAndTwoLines()
    {
        var meeting = Meeting("a", At(15, 14), At(15, 16), "Конференция");

        var text = new MeetingFormatter(Moscow).FormatMeeting(meeting);
        var lines = text.Split('\n');

        Assert.AreEqual(2, lines.Length, "expected the time/subject line and the people line");
        StringAssert.StartsWith(lines[0], "14:00–16:00");
        StringAssert.Contains(lines[0], "Конференция");
        StringAssert.Contains(lines[1], "с кем:");
        StringAssert.Contains(lines[1], "Иванов");
    }

    [TestMethod]
    public void FormatMeeting_AddsLinkLineWhenOnlineMeetingUrlPresent()
    {
        var meeting = Meeting("a", At(15, 14), At(15, 16), onlineUrl: "https://example.com/j/123");

        var text = new MeetingFormatter(Moscow).FormatMeeting(meeting);

        StringAssert.Contains(text, "zoom: https://example.com/j/123");
    }

    [TestMethod]
    public void FormatMeeting_TruncatesLongSubjectToKeepTheLayoutNarrow()
    {
        var meeting = Meeting("a", At(15, 14), At(15, 16), new string('x', 120));

        var text = new MeetingFormatter(Moscow).FormatMeeting(meeting);
        var head = text.Split('\n')[0];

        Assert.IsTrue(head.Length <= 40, $"head line is {head.Length} chars: {head}");
        StringAssert.EndsWith(head, "…");
    }

    [TestMethod]
    public void FormatToday_IncludesHeaderAndAllMeetings()
    {
        var meetings = new[]
        {
            Meeting("a", At(15, 14), At(15, 16)),
            Meeting("b", At(15, 18), At(15, 19))
        };

        var text = new MeetingFormatter(Moscow).FormatToday(meetings, new DateOnly(2026, 10, 15));

        StringAssert.StartsWith(text, "Сводка на");
        StringAssert.Contains(text, "14:00–16:00");
        StringAssert.Contains(text, "18:00–19:00");
    }

    [TestMethod]
    public void FormatTomorrow_NamesTheDay()
    {
        var meetings = new[] { Meeting("a", At(16, 10), At(16, 11)) };

        var text = new MeetingFormatter(Moscow).FormatTomorrow(meetings, new DateOnly(2026, 10, 15));

        StringAssert.StartsWith(text, "Встречи завтра");
        StringAssert.Contains(text, "10:00–11:00");
    }

    [TestMethod]
    public void Format_Week_ProducesOneMessagePerDayWithMeetings()
    {
        var meetings = new[]
        {
            Meeting("day1", At(15, 14), At(15, 16)),
            Meeting("day2", At(17, 10), At(17, 11)),
            Meeting("day2b", At(17, 15), At(17, 16))
        };

        var messages = new MeetingFormatter(Moscow).Format(MeetingPeriod.Week, meetings, new DateOnly(2026, 10, 15));

        Assert.AreEqual(2, messages.Count);
        StringAssert.StartsWith(messages[0], "Неделя с 15.10");
        StringAssert.Contains(messages[0], "чт 15.10", "the day label keeps the dot inside the date");
        StringAssert.Contains(messages[1], "10:00–11:00");
    }

    [TestMethod]
    public void Format_Week_SkipsDaysWithoutMeetings()
    {
        var meetings = new[] { Meeting("d1", At(16, 10), At(16, 11)) };

        var messages = new MeetingFormatter(Moscow).Format(MeetingPeriod.Week, meetings, new DateOnly(2026, 10, 15));

        Assert.AreEqual(1, messages.Count, "empty days produce no message");
    }

    [TestMethod]
    public void Format_EmptySelection_ProducesNoMessages()
    {
        var messages = new MeetingFormatter(Moscow).Format(MeetingPeriod.Week, [], new DateOnly(2026, 10, 15));

        Assert.AreEqual(0, messages.Count);
    }

    [TestMethod]
    public void Format_Week_SplitsADayThatExceedsTheMessageBudget()
    {
        var many = Enumerable.Range(0, 200)
            .Select(i => Meeting($"m{i}", At(15, 9), At(15, 9), new string('y', 120)))
            .ToList();

        var messages = new MeetingFormatter(Moscow).Format(MeetingPeriod.Week, many, new DateOnly(2026, 10, 15));

        Assert.IsTrue(messages.Count > 1, "a very long day must be split");
        Assert.IsTrue(messages.All(m => m.Length <= 4096), "no message may exceed the Telegram limit");
        StringAssert.Contains(messages[1], "продолжение");
    }

    [TestMethod]
    public void Truncate_KeepsShortTextAndMarksCutText()
    {
        Assert.AreEqual("abc", MeetingFormatter.Truncate("abc", 10));
        Assert.AreEqual("ab…", MeetingFormatter.Truncate("abcdef", 3));
        Assert.AreEqual(string.Empty, MeetingFormatter.Truncate(null, 10));
    }
}