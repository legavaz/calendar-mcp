using Microsoft.Extensions.Configuration;

namespace CalendarMcp.Reminders;

/// <summary>
/// Which set of meetings a request is about. Digest and the bot commands all
/// render through the same formatter, so they share this period vocabulary.
/// </summary>
public enum MeetingPeriod
{
    /// <summary>Meetings left for the current day.</summary>
    Today,

    /// <summary>All meetings on the next day.</summary>
    Tomorrow,

    /// <summary>Meetings across the next seven days, starting today.</summary>
    Week
}

/// <summary>
/// Settings for the morning digest: when it goes out, how late it may still be
/// sent, and how many days the weekly view covers.
/// </summary>
public sealed class DigestOptions
{
    /// <summary>Whether the daily digest is sent at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Local send time in <c>HH:mm</c> form.</summary>
    public TimeSpan Time { get; set; } = new(8, 30, 0);

    /// <summary>
    /// Local time after which the digest for the day is abandoned. A machine
    /// that wakes up in the afternoon gets no digest rather than a stale one.
    /// </summary>
    public TimeSpan Cutoff { get; set; } = new(12, 0, 0);

    /// <summary>How many days the weekly view spans.</summary>
    public int WeekDays { get; set; } = 7;
}

/// <summary>
/// Telegram delivery settings. A single chat is served, so there is no chat list
/// and no per-channel routing.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>Whether Telegram delivery is used.</summary>
    public bool Enabled { get; set; }

    /// <summary>Bot token issued by BotFather.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Numeric chat ID that receives every message.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Long-poll timeout in seconds used when waiting for commands.</summary>
    public int PollTimeoutSeconds { get; set; } = 50;

    /// <summary>Whether the bot answers <c>/today</c>, <c>/tomorrow</c>, <c>/week</c>.</summary>
    public bool CommandsEnabled { get; set; } = true;
}

/// <summary>
/// Root options for the reminders host, bound from the <c>Notifications</c>
/// section of <c>appsettings.json</c>.
/// </summary>
public sealed class ReminderOptions
{
    /// <summary>Section name inside <c>appsettings.json</c>.</summary>
    public const string SectionName = "Notifications";

    /// <summary>Master switch for the whole host.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Account whose calendar is polled. Required.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Minutes before a meeting at which a reminder is sent.</summary>
    public List<int> LeadMinutes { get; set; } = [];

    /// <summary>Lead times used when the configuration does not define any.</summary>
    public static int[] DefaultLeadMinutes => [60, 15];

    /// <summary>
    /// Slack applied on both edges of a reminder window so a slightly early or
    /// late tick still fires. Without it a meeting can be missed entirely when
    /// the tick lands a minute past the boundary.
    /// </summary>
    public int LeadToleranceMinutes { get; set; } = 2;

    /// <summary>IANA time zone all windows and displayed times use.</summary>
    public string TimeZone { get; set; } = "Europe/Moscow";

    /// <summary>Digest settings.</summary>
    public DigestOptions Digest { get; set; } = new();

    /// <summary>Telegram settings.</summary>
    public TelegramOptions Telegram { get; set; } = new();

    /// <summary>State file, relative to the config directory unless rooted.</summary>
    public string StateFile { get; set; } = "logs/reminder-state.json";

    /// <summary>Seconds between reminder-evaluation ticks inside the process.</summary>
    public int TickSeconds { get; set; } = 30;

    /// <summary>Minutes between calendar refreshes. Ticks reuse the cached list.</summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>True when the settings are complete enough to run.</summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(AccountId)
        && Telegram.Enabled
        && !string.IsNullOrWhiteSpace(Telegram.BotToken)
        && !string.IsNullOrWhiteSpace(Telegram.ChatId);

    /// <summary>
    /// Reads options from the config directory that hosts <c>appsettings.json</c>,
    /// falling back to defaults for anything absent.
    /// </summary>
    /// <param name="configDirectory">Directory containing <c>appsettings.json</c>.</param>
    public static ReminderOptions Load(string configDirectory)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(configDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var options = new ReminderOptions();
        configuration.GetSection(SectionName).Bind(options);

        // The configuration binder appends to a pre-populated list instead of
        // replacing it, which would double the defaults. Bind into an empty list
        // and only then apply the fallback.
        if (options.LeadMinutes.Count == 0)
        {
            options.LeadMinutes = [.. DefaultLeadMinutes];
        }

        return options;
    }

    /// <summary>
    /// Resolves the state file to an absolute path. Relative values land under the
    /// config directory, which is Git-ignored, so reminders never write into the
    /// source tree by accident.
    /// </summary>
    /// <param name="configDirectory">Directory containing <c>appsettings.json</c>.</param>
    public string ResolveStatePath(string configDirectory) =>
        Path.IsPathRooted(StateFile) ? StateFile : Path.Combine(configDirectory, StateFile);

    /// <summary>Resolves <see cref="TimeZone"/>, falling back to UTC when unknown.</summary>
    public TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}