using CalendarMcp.Reminders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalendarMcp.Tests.Reminders;

/// <summary>
/// Covers parsing of bot commands and plain Russian requests. A message that
/// matches nothing must fall through to the help reply rather than guessing.
/// </summary>
[TestClass]
public class CommandInterpreterTests
{
    [DataTestMethod]
    [DataRow("/today", MeetingPeriod.Today)]
    [DataRow("/now", MeetingPeriod.Today)]
    [DataRow("/tomorrow", MeetingPeriod.Tomorrow)]
    [DataRow("/week", MeetingPeriod.Week)]
    [DataRow("/TODAY", MeetingPeriod.Today)]
    [DataRow("  /Week  ", MeetingPeriod.Week)]
    public void TryParse_Commands_ReturnExpectedPeriod(string text, MeetingPeriod expected)
    {
        Assert.IsTrue(CommandInterpreter.TryParse(text, out var period));
        Assert.AreEqual(expected, period);
    }

    [DataTestMethod]
    [DataRow("встречи завтра", MeetingPeriod.Tomorrow)]
    [DataRow("Встречи Завтра", MeetingPeriod.Tomorrow)]
    [DataRow("что на завтрашний день", MeetingPeriod.Tomorrow)]
    [DataRow("что на неделе", MeetingPeriod.Week)]
    [DataRow("встречи на неделю", MeetingPeriod.Week)]
    [DataRow("неделя", MeetingPeriod.Week)]
    [DataRow("что у меня сегодня", MeetingPeriod.Today)]
    [DataRow("встречи на сегодня", MeetingPeriod.Today)]
    public void TryParse_RussianPhrases_ReturnExpectedPeriod(string text, MeetingPeriod expected)
    {
        Assert.IsTrue(CommandInterpreter.TryParse(text, out var period));
        Assert.AreEqual(expected, period);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("спасибо")]
    [DataRow("привет")]
    [DataRow("/unknown")]
    [DataRow("/weekday")]
    public void TryParse_UnrelatedText_ReturnsFalse(string? text)
    {
        Assert.IsFalse(CommandInterpreter.TryParse(text, out _));
    }

    [TestMethod]
    public void HelpText_MentionsEveryCommand()
    {
        var help = CommandInterpreter.HelpText;

        StringAssert.Contains(help, "/today");
        StringAssert.Contains(help, "/tomorrow");
        StringAssert.Contains(help, "/week");
    }
}