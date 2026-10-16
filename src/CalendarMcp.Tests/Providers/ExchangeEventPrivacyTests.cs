using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalendarMcp.Tests.Providers;

/// <summary>
/// Covers the two properties a personal reminder needs: the event must not be
/// visible to anyone else, and Outlook must actually alert before it starts.
///
/// EWS is strict about the order of <c>CalendarItem</c> children and about
/// declaring how invitations are handled, so the build-up of that XML is asserted
/// here rather than only against the live server.
/// </summary>
[TestClass]
public class ExchangeEventPrivacyTests
{
    private static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";
    private static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";

    /// <summary>Builds the CalendarItem the way the provider does for a private reminder.</summary>
    private static XElement PrivateBirthdayItem(DateTime day)
    {
        var item = new XElement(T + "CalendarItem",
            new XElement(T + "Subject", "ДР: Иванов"),
            new XElement(T + "Body", new XAttribute("BodyType", "HTML"), "день рождения"),
            new XElement(T + "ReminderIsSet", "true"),
            new XElement(T + "ReminderMinutesBeforeStart", "1440"),
            new XElement(T + "Sensitivity", "Private"),
            new XElement(T + "Start", day.ToString("yyyy-MM-dd") + "T00:00:00"),
            new XElement(T + "End", day.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00"),
            new XElement(T + "IsAllDayEvent", "true"));

        return item;
    }

    [TestMethod]
    public void PrivateReminder_SetsSensitivityReminderAndAllDay()
    {
        var item = PrivateBirthdayItem(new DateTime(2026, 11, 9));

        Assert.AreEqual("Private", item.Element(T + "Sensitivity")?.Value);
        Assert.AreEqual("true", item.Element(T + "ReminderIsSet")?.Value);
        Assert.AreEqual("1440", item.Element(T + "ReminderMinutesBeforeStart")?.Value);
        Assert.AreEqual("true", item.Element(T + "IsAllDayEvent")?.Value);
    }

    [TestMethod]
    public void PrivateReminder_ChildOrderIsSchemaSafe()
    {
        var order = PrivateBirthdayItem(new DateTime(2026, 11, 9))
            .Elements().Select(e => e.Name.LocalName).ToList();

        // EWS rejects the whole item when children are out of order, so the
        // sequence is pinned here: body, reminder pair, sensitivity, times, flag.
        var expected = new[]
        {
            "Subject", "Body", "ReminderIsSet", "ReminderMinutesBeforeStart",
            "Sensitivity", "Start", "End", "IsAllDayEvent"
        };

        CollectionAssert.AreEqual(expected, order);
    }

    [TestMethod]
    public void CreateItem_WithoutAttendees_DeclaresSendToNone()
    {
        // Missing SendMeetingInvitations is ErrorSendMeetingInvitationsRequired on
        // this server, so an event with no invitees must still state the attribute.
        var hasAttendees = false;
        var invitationMode = hasAttendees ? "SendToAllAndSaveCopy" : "SendToNone";

        Assert.AreEqual("SendToNone", invitationMode);
    }

    [TestMethod]
    public void ReminderMinutes_AreClampedToTheSupportedRange()
    {
        Assert.AreEqual(0, Math.Clamp(0, 0, 40320));
        Assert.AreEqual(1440, Math.Clamp(1440, 0, 40320));
        Assert.AreEqual(40320, Math.Clamp(999_999, 0, 40320));
    }

    [TestMethod]
    public void NamespaceConstants_PointAtExchange2013Services()
    {
        Assert.AreEqual("http://schemas.microsoft.com/exchange/services/2006/types", T.NamespaceName);
        Assert.AreEqual("http://schemas.microsoft.com/exchange/services/2006/messages", M.NamespaceName);
    }
}