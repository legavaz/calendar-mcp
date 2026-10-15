using CalendarMcp.Reminders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalendarMcp.Tests.Reminders;

/// <summary>
/// Covers the file that stops reminders from repeating. Dedup is what makes a
/// restart safe, so the store has to survive a reload and refuse double marks.
/// </summary>
[TestClass]
public class ReminderStateStoreTests
{
    private static ReminderStateStore CreateStore(out string directory, out string path)
    {
        directory = Path.Combine(Path.GetTempPath(), "calendar-mcp-tests", Guid.NewGuid().ToString("N"));
        path = Path.Combine(directory, "logs", "reminder-state.json");
        return new ReminderStateStore(path, NullLogger<ReminderStateStore>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "calendar-mcp-tests");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task Reminder_IsRecordedOncePerLeadTime()
    {
        var store = CreateStore(out _, out _);
        await store.LoadAsync();
        var now = DateTimeOffset.UtcNow;

        Assert.IsFalse(await store.IsReminderSentAsync("event-1", 15));
        Assert.IsTrue(await store.TryMarkReminderSentAsync("event-1", 15, now));
        Assert.IsTrue(await store.IsReminderSentAsync("event-1", 15));

        Assert.IsFalse(await store.TryMarkReminderSentAsync("event-1", 15, now), "a second mark must be refused");
        Assert.IsTrue(await store.TryMarkReminderSentAsync("event-1", 60, now), "a different lead time is a separate reminder");
    }

    [TestMethod]
    public async Task Reminder_IsScopedToItsEvent()
    {
        var store = CreateStore(out _, out _);
        await store.LoadAsync();

        await store.TryMarkReminderSentAsync("event-1", 15, DateTimeOffset.UtcNow);

        Assert.IsFalse(await store.IsReminderSentAsync("event-2", 15));
    }

    [TestMethod]
    public async Task State_SurvivesReload()
    {
        var store = CreateStore(out _, out var path);
        await store.LoadAsync();
        await store.TryMarkReminderSentAsync("event-1", 15, DateTimeOffset.UtcNow);
        await store.MarkDigestSentAsync(new DateOnly(2026, 10, 15));
        await store.SetUpdateOffsetAsync(42);

        var reopened = new ReminderStateStore(path, NullLogger<ReminderStateStore>.Instance);
        await reopened.LoadAsync();

        Assert.IsTrue(await reopened.IsReminderSentAsync("event-1", 15));
        Assert.IsTrue(await reopened.IsDigestSentAsync(new DateOnly(2026, 10, 15)));
        Assert.AreEqual(42, await reopened.GetUpdateOffsetAsync());
    }

    [TestMethod]
    public async Task Digest_IsTrackedPerDate()
    {
        var store = CreateStore(out _, out _);
        await store.LoadAsync();

        Assert.IsFalse(await store.IsDigestSentAsync(new DateOnly(2026, 10, 15)));
        await store.MarkDigestSentAsync(new DateOnly(2026, 10, 15));

        Assert.IsTrue(await store.IsDigestSentAsync(new DateOnly(2026, 10, 15)));
        Assert.IsFalse(await store.IsDigestSentAsync(new DateOnly(2026, 10, 16)));
    }

    [TestMethod]
    public async Task UpdateOffset_NeverMovesBackwards()
    {
        var store = CreateStore(out _, out _);
        await store.LoadAsync();

        await store.SetUpdateOffsetAsync(100);
        await store.SetUpdateOffsetAsync(40);

        Assert.AreEqual(100, await store.GetUpdateOffsetAsync());
    }

    [TestMethod]
    public async Task CorruptFile_StartsFromEmptyStateInsteadOfThrowing()
    {
        var store = CreateStore(out _, out var path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ this is not json");

        await store.LoadAsync();

        Assert.IsFalse(await store.IsReminderSentAsync("event-1", 15));
        Assert.AreEqual(0, await store.GetUpdateOffsetAsync());
    }

    [TestMethod]
    public async Task Clear_RemovesTheFileAndForgetsEverything()
    {
        var store = CreateStore(out _, out var path);
        await store.LoadAsync();
        await store.TryMarkReminderSentAsync("event-1", 15, DateTimeOffset.UtcNow);

        await store.ClearAsync();

        Assert.IsFalse(File.Exists(path));
        Assert.IsFalse(await store.IsReminderSentAsync("event-1", 15));
    }
}