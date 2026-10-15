using CalendarMcp.Reminders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalendarMcp.Tests.Reminders;

/// <summary>
/// Covers binding of the Notifications section. The configuration binder merges
/// lists rather than replacing them, so defaults are the classic place to end up
/// with duplicated values.
/// </summary>
[TestClass]
public class ReminderOptionsTests
{
    private static string WriteConfig(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "calendar-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), json);
        return directory;
    }

    [TestCleanup]
    public void Cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "calendar-mcp-tests");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void Load_ReadsEveryField()
    {
        var directory = WriteConfig("""
        {
          "Notifications": {
            "Enabled": true,
            "AccountId": "exchange-work",
            "LeadMinutes": [30],
            "TimeZone": "Europe/Kaliningrad",
            "Digest": { "Enabled": true, "Time": "09:15:00", "Cutoff": "13:00:00", "WeekDays": 5 },
            "Telegram": { "Enabled": true, "BotToken": "token", "ChatId": "42", "PollTimeoutSeconds": 30 },
            "StateFile": "state.json"
          }
        }
        """);

        var options = ReminderOptions.Load(directory);

        Assert.AreEqual("exchange-work", options.AccountId);
        CollectionAssert.AreEqual(new[] { 30 }, options.LeadMinutes);
        Assert.AreEqual("Europe/Kaliningrad", options.TimeZone);
        Assert.AreEqual(new TimeSpan(9, 15, 0), options.Digest.Time);
        Assert.AreEqual(new TimeSpan(13, 0, 0), options.Digest.Cutoff);
        Assert.AreEqual(5, options.Digest.WeekDays);
        Assert.AreEqual("42", options.Telegram.ChatId);
        Assert.AreEqual(30, options.Telegram.PollTimeoutSeconds);
        Assert.AreEqual("state.json", options.StateFile);
        Assert.IsTrue(options.IsConfigured);
    }

    [TestMethod]
    public void Load_ConfiguredLeadMinutes_AreNotDoubled()
    {
        var directory = WriteConfig("""
        {
          "Notifications": {
            "Enabled": true,
            "AccountId": "a",
            "LeadMinutes": [60, 15],
            "Telegram": { "Enabled": true, "BotToken": "t", "ChatId": "1" }
          }
        }
        """);

        var options = ReminderOptions.Load(directory);

        CollectionAssert.AreEqual(new[] { 60, 15 }, options.LeadMinutes);
    }

    [TestMethod]
    public void Load_MissingLeadMinutes_FallBackToDefaults()
    {
        var directory = WriteConfig("""
        {
          "Notifications": {
            "Enabled": true,
            "AccountId": "a",
            "Telegram": { "Enabled": true, "BotToken": "t", "ChatId": "1" }
          }
        }
        """);

        var options = ReminderOptions.Load(directory);

        CollectionAssert.AreEqual(ReminderOptions.DefaultLeadMinutes, options.LeadMinutes);
    }

    [TestMethod]
    public void Load_MissingFile_UsesSafeDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "calendar-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var options = ReminderOptions.Load(directory);

        Assert.IsFalse(options.IsConfigured, "nothing is configured without a file");
        Assert.AreEqual("Europe/Moscow", options.TimeZone);
        Assert.AreEqual(new TimeSpan(8, 30, 0), options.Digest.Time);
    }

    [TestMethod]
    public void IsConfigured_RequiresTokenAndChatId()
    {
        var directory = WriteConfig("""
        {
          "Notifications": {
            "Enabled": true,
            "AccountId": "a",
            "Telegram": { "Enabled": true, "BotToken": "", "ChatId": "" }
          }
        }
        """);

        Assert.IsFalse(ReminderOptions.Load(directory).IsConfigured);
    }

    [TestMethod]
    public void ResolveStatePath_RelativePathLandsUnderTheConfigDirectory()
    {
        var directory = WriteConfig("{}");

        var options = new ReminderOptions { StateFile = "logs/state.json" };
        var resolved = options.ResolveStatePath(directory);

        StringAssert.StartsWith(resolved, directory);
        StringAssert.EndsWith(resolved, "logs/state.json");
    }

    [TestMethod]
    public void ResolveStatePath_KeepsRootedPaths()
    {
        var options = new ReminderOptions { StateFile = @"C:\custom\state.json" };

        Assert.AreEqual(@"C:\custom\state.json", options.ResolveStatePath("ignored"));
    }

    [TestMethod]
    public void ResolveTimeZone_UnknownZoneFallsBackToUtc()
    {
        var options = new ReminderOptions { TimeZone = "Mars/Olympus_Mons" };

        Assert.AreEqual(TimeZoneInfo.Utc, options.ResolveTimeZone());
    }
}