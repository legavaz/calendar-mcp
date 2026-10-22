using CalendarMcp.Reminders.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalendarMcp.Tests.Reminders;

/// <summary>
/// Covers the log file naming. A long-running host used to keep the file name it
/// computed at startup, so after a few days the "today" file simply did not exist
/// and an empty calendar looked like a dead process.
/// </summary>
[TestClass]
public class FileLoggerProviderTests
{
    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), "calendar-mcp-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "calendar-mcp-tests");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public void PathFor_UsesOneFilePerDay()
    {
        var provider = new FileLoggerProvider(TempDirectory(), "reminders", LogLevel.Information);

        Assert.AreEqual("reminders-20261019.log", Path.GetFileName(provider.PathFor(new DateOnly(2026, 10, 19))));
        Assert.AreEqual("reminders-20261020.log", Path.GetFileName(provider.PathFor(new DateOnly(2026, 10, 20))));
    }

    [TestMethod]
    public void PathFor_KeepsFilesInTheConfiguredDirectory()
    {
        var directory = TempDirectory();
        var provider = new FileLoggerProvider(directory, "reminders", LogLevel.Information);

        var path = provider.PathFor(new DateOnly(2026, 10, 20));

        Assert.AreEqual(directory, Path.GetDirectoryName(path));
    }

    [TestMethod]
    public void Log_WritesToTheCurrentDayFileNotTheStartupOne()
    {
        var directory = TempDirectory();
        var provider = new FileLoggerProvider(directory, "reminders", LogLevel.Information);
        var logger = provider.CreateLogger("test");

        logger.LogInformation("first");

        var today = DateOnly.FromDateTime(DateTime.Now);
        Assert.IsTrue(File.Exists(provider.PathFor(today)), "the entry must land in today's file");
        StringAssert.Contains(File.ReadAllText(provider.PathFor(today)), "first");
    }

    [TestMethod]
    public void Log_BelowMinimumLevel_IsNotWritten()
    {
        var directory = TempDirectory();
        var provider = new FileLoggerProvider(directory, "reminders", LogLevel.Warning);
        var logger = provider.CreateLogger("test");

        logger.LogInformation("ignored");
        logger.LogWarning("kept");

        var content = File.ReadAllText(provider.PathFor(DateOnly.FromDateTime(DateTime.Now)));
        Assert.IsFalse(content.Contains("ignored"));
        StringAssert.Contains(content, "kept");
    }
}