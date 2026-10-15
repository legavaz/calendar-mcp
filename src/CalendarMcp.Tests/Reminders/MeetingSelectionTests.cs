using CalendarMcp.Core.Models;
using CalendarMcp.Reminders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalendarMcp.Tests.Reminders;

/// <summary>
/// Covers which meetings reach a notification and which are filtered out.
/// </summary>
[TestClass]
public class MeetingSelectionTests
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
        bool allDay = false,
        bool cancelled = false,
        string? organizer = null,
        string[]? attendees = null) =>
        new()
        {
            Id = id,
            AccountId = "acct",
            CalendarId = "calendar",
            Subject = id,
            Start = start,
            End = end,
            IsAllDay = allDay,
            IsCancelled = cancelled,
            Organizer = organizer ?? string.Empty,
            Attendees = attendees?.ToList() ?? []
        };

    [TestMethod]
    public void IsAnnounceable_AllDayAndCancelled_AreExcluded()
    {
        Assert.IsFalse(MeetingSelection.IsAnnounceable(Meeting("a", At(15, 10), At(15, 11), allDay: true)));
        Assert.IsFalse(MeetingSelection.IsAnnounceable(Meeting("b", At(15, 10), At(15, 11), cancelled: true)));
        Assert.IsTrue(MeetingSelection.IsAnnounceable(Meeting("c", At(15, 10), At(15, 11))));
    }

    [TestMethod]
    public void InvolvesOwner_MatchesOrganizerOrAttendee()
    {
        var meeting = Meeting("a", At(15, 10), At(15, 11), organizer: "boss@example.com", attendees: ["me@example.com"]);

        Assert.IsTrue(MeetingSelection.InvolvesOwner(meeting, "ME@example.com"), "attendee match is case-insensitive");
        Assert.IsTrue(MeetingSelection.InvolvesOwner(meeting, "boss@example.com"));
        Assert.IsFalse(MeetingSelection.InvolvesOwner(meeting, "someone-else@example.com"));
    }

    [TestMethod]
    public void InvolvesOwner_UnknownOwner_KeepsEverything()
    {
        var meeting = Meeting("a", At(15, 10), At(15, 11), organizer: "boss@example.com");

        Assert.IsTrue(MeetingSelection.InvolvesOwner(meeting, null));
        Assert.IsTrue(MeetingSelection.InvolvesOwner(meeting, "  "));
    }

    [TestMethod]
    public void InvolvesOwner_NoComparableAddress_IsKept()
    {
        // Exchange 2019 on-prem returns an X500 distinguished name as organizer and
        // leaves the attendee lists empty. Filtering on that would drop everything.
        var meeting = Meeting(
            "a",
            At(15, 10),
            At(15, 11),
            organizer: "/O=ISTK/OU=EXCHANGE ADMINISTRATIVE GROUP/CN=RECIPIENTS/CN=RECIPIENT");

        Assert.IsTrue(MeetingSelection.InvolvesOwner(meeting, "me@example.com"));
    }

    [TestMethod]
    public void InvolvesOwner_LegacyOrganizerWithForeignAttendees_IsDropped()
    {
        var meeting = Meeting(
            "a",
            At(15, 10),
            At(15, 11),
            organizer: "/O=ISTK/CN=RECIPIENTS/CN=SOMEONE",
            attendees: ["other@example.com"]);

        Assert.IsFalse(MeetingSelection.InvolvesOwner(meeting, "me@example.com"));
    }

    [TestMethod]
    public void ForPeriod_Today_DropsMeetingsThatAlreadyStarted()
    {
        var pool = new[]
        {
            Meeting("past", At(15, 9), At(15, 10)),
            Meeting("future", At(15, 16), At(15, 17))
        };

        var selected = MeetingSelection.ForPeriod(pool, MeetingPeriod.Today, At(15, 12), Moscow, null);

        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual("future", selected[0].Id);
    }

    [TestMethod]
    public void ForPeriod_Today_KeepsAMeetingThatIsUnderway()
    {
        var pool = new[] { Meeting("running", At(15, 11), At(15, 14)) };

        var selected = MeetingSelection.ForPeriod(pool, MeetingPeriod.Today, At(15, 12), Moscow, null);

        Assert.AreEqual(0, selected.Count);
    }

    [TestMethod]
    public void ForPeriod_Tomorrow_ReturnsOnlyTomorrow()
    {
        var pool = new[]
        {
            Meeting("today", At(15, 20), At(15, 21)),
            Meeting("tomorrow", At(16, 10), At(16, 11)),
            Meeting("later", At(17, 10), At(17, 11))
        };

        var selected = MeetingSelection.ForPeriod(pool, MeetingPeriod.Tomorrow, At(15, 12), Moscow, null);

        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual("tomorrow", selected[0].Id);
    }

    [TestMethod]
    public void ForPeriod_Week_SpansSevenDaysAndSorts()
    {
        var pool = new[]
        {
            Meeting("day8", At(23, 10), At(23, 11)),
            Meeting("day3", At(18, 9), At(18, 10)),
            Meeting("day1", At(16, 9), At(16, 10))
        };

        var selected = MeetingSelection.ForPeriod(pool, MeetingPeriod.Week, At(15, 12), Moscow, null, weekDays: 7);

        CollectionAssert.AreEqual(new[] { "day1", "day3" }, selected.Select(m => m.Id).ToArray());
    }

    [TestMethod]
    public void ForPeriod_ExcludesOtherPeopleAndAllDay()
    {
        var foreign = Meeting("foreign", At(16, 10), At(16, 11), organizer: "boss@example.com", attendees: ["someone@example.com"]);

        var pool = new[]
        {
            foreign,
            Meeting("allday", At(16, 10), At(16, 11), allDay: true)
        };

        var selected = MeetingSelection.ForPeriod(pool, MeetingPeriod.Tomorrow, At(15, 12), Moscow, "me@example.com");

        Assert.AreEqual(0, selected.Count);
    }
}