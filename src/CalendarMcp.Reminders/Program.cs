using CalendarMcp.Core.Configuration;
using CalendarMcp.Reminders;
using CalendarMcp.Reminders.Logging;
using CalendarMcp.Reminders.Notification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

var commandLine = CommandLine.Parse(args);

if (commandLine.ShowHelp)
{
    Console.WriteLine(CommandLine.HelpText);
    return 0;
}

var configDirectory = ConfigurationPaths.GetDataDirectory();
Directory.CreateDirectory(configDirectory);

var options = ReminderOptions.Load(configDirectory);

// One provider instance feeds both the startup logger and the DI logging, so the
// console and the log file always tell the same story.
var fileLogger = new FileLoggerProvider(
    Path.Combine(configDirectory, "logs", $"reminders-{DateTime.Now:yyyyMMdd}.log"),
    commandLine.Verbose ? LogLevel.Debug : LogLevel.Information);

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(o => o.SingleLine = true);
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddFilter("System.Net.Http.HttpClient", commandLine.Verbose ? LogLevel.Debug : LogLevel.Warning);
    builder.AddProvider(fileLogger);
});

var logger = loggerFactory.CreateLogger("CalendarMcp.Reminders");

if (!options.Enabled)
{
    logger.LogInformation("Notifications.Enabled is false; nothing to do");
    return 0;
}

if (!options.IsConfigured)
{
    logger.LogError(
        "Notifications is not configured. Required: AccountId, Telegram.Enabled, Telegram.BotToken, Telegram.ChatId in {Directory}",
        configDirectory);
    return 1;
}

var statePath = options.ResolveStatePath(configDirectory);

var services = new ServiceCollection();
services.AddLogging(b =>
{
    b.ClearProviders();
    b.AddSimpleConsole(o => o.SingleLine = true);

    // A long-running host is not an interactive session: its own progress notes
    // go to the console for troubleshooting, everything else only to the file.
    b.SetMinimumLevel(LogLevel.Information);

    // HttpClient logs every request at Information. With a calendar poll and a
    // command long-poll in the loop that is pure noise, so it is silenced unless
    // --verbose asks for it.
    b.AddFilter("System.Net.Http.HttpClient", commandLine.Verbose ? LogLevel.Debug : LogLevel.Warning);

    b.AddProvider(fileLogger);
});
var configPath = Path.Combine(configDirectory, "appsettings.json");

var hostConfiguration = new ConfigurationBuilder()
    .SetBasePath(configDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();

// AccountRegistry reads its accounts through IOptionsMonitor<CalendarMcpConfiguration>,
// so the CalendarMcp section has to be bound exactly like the MCP server binds it.
// Without this the registry starts empty and the host reports no accounts.
services.Configure<CalendarMcpConfiguration>(hostConfiguration.GetSection("CalendarMcp"));

services.AddSingleton(options);
services.AddSingleton(new TimeZoneInfoSettings(options.ResolveTimeZone(), options.Digest.WeekDays));
services.AddCalendarMcpCore();

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(options.Telegram.PollTimeoutSeconds + 20) };
services.AddSingleton(httpClient);
services.AddSingleton<TelegramClient>();
services.AddSingleton<IMeetingSource, MeetingSource>();
services.AddSingleton(sp => new MeetingFormatter(
    sp.GetRequiredService<TimeZoneInfoSettings>().Zone,
    sp.GetRequiredService<TimeZoneInfoSettings>().WeekDays));
services.AddSingleton<ReminderStateStore>(sp => new ReminderStateStore(
    statePath,
    sp.GetRequiredService<ILogger<ReminderStateStore>>()));
services.AddSingleton<INotifier>(sp => commandLine.DryRun
    ? new NullNotifier()
    : new TelegramNotifier(
        sp.GetRequiredService<TelegramClient>(),
        options.Telegram.BotToken,
        options.Telegram.ChatId));
services.AddSingleton<ReminderService>();
services.AddSingleton<TelegramCommandService>();

using var provider = services.BuildServiceProvider();

var state = provider.GetRequiredService<ReminderStateStore>();
await state.LoadAsync();

var source = provider.GetRequiredService<IMeetingSource>();
await source.ResolveAccountAsync();

if (commandLine.ClearState)
{
    await state.ClearAsync();
    logger.LogInformation("Reminder state cleared at {Path}", statePath);
    return 0;
}

if (commandLine.TestOnly)
{
    var notifier = provider.GetRequiredService<INotifier>();
    await notifier.SendAsync(
        $"Проверка связи. {DateTimeOffset.Now:dd.MM HH:mm}. Если вы это видите, доставка настроена.",
        CancellationToken.None);
    logger.LogInformation("Test message sent");
    return 0;
}

var reminders = provider.GetRequiredService<ReminderService>();
var commands = provider.GetRequiredService<TelegramCommandService>();

if (commandLine.Once)
{
    var sent = await reminders.RunPassAsync(DateTimeOffset.UtcNow, commandLine.DryRun);
    logger.LogInformation("Single pass finished; {Count} message(s) sent", sent);
    return 0;
}

if (commandLine.PrintWeek)
{
    var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, options.ResolveTimeZone());
    var today = DateOnly.FromDateTime(localNow.DateTime);

    await reminders.EnsureCacheAsync(DateTimeOffset.UtcNow);
    var formatter = provider.GetRequiredService<MeetingFormatter>();
    var selected = MeetingSelection.ForPeriod(
        reminders.CachedMeetings,
        MeetingPeriod.Week,
        localNow,
        options.ResolveTimeZone(),
        source.OwnEmail,
        options.Digest.WeekDays);

    Console.WriteLine($"Loaded {reminders.CachedMeetings.Count} meetings; {selected.Count} in the next {options.Digest.WeekDays} days.");
    foreach (var message in formatter.Format(MeetingPeriod.Week, selected, today))
    {
        Console.WriteLine();
        Console.WriteLine(message);
    }

    return 0;
}

if (commandLine.DigestNow)
{
    var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, options.ResolveTimeZone());
    var today = DateOnly.FromDateTime(localNow.DateTime);

    await reminders.EnsureCacheAsync(DateTimeOffset.UtcNow);
    var formatter = provider.GetRequiredService<MeetingFormatter>();
    var meetings = MeetingSelection.ForPeriod(
        reminders.CachedMeetings,
        MeetingPeriod.Today,
        localNow,
        options.ResolveTimeZone(),
        source.OwnEmail);

    var notifier = provider.GetRequiredService<INotifier>();
    var text = meetings.Count == 0
        ? TelegramCommandService.NoMeetingsText(MeetingPeriod.Today, today)
        : formatter.FormatToday(meetings, today);

    if (commandLine.DryRun)
    {
        Console.WriteLine(text);
        return 0;
    }

    await notifier.SendAsync(text);
    logger.LogInformation("Digest preview sent for {Date} without recording state", today);
    return 0;
}

logger.LogInformation(
    "Reminder host started for account {AccountId}; leads {Leads}, digest {Digest}, tz {Zone}",
    options.AccountId,
    string.Join("/", options.LeadMinutes),
    options.Digest.Time.ToString(@"hh\:mm"),
    options.TimeZone);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

var tick = TimeSpan.FromSeconds(Math.Max(5, options.TickSeconds));

var reminderLoop = Task.Run(async () =>
{
    while (!shutdown.IsCancellationRequested)
    {
        try
        {
            await reminders.RunPassAsync(DateTimeOffset.UtcNow, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reminder pass failed; continuing");
        }

        try
        {
            await Task.Delay(tick, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }
});

var commandLoop = Task.Run(() => commands.RunAsync(DateTimeOffset.UtcNow, shutdown.Token));

await Task.WhenAll(reminderLoop, commandLoop);
return 0;

/// <summary>Parsed command line for the reminders host.</summary>
internal sealed record CommandLine(bool Once, bool DryRun, bool TestOnly, bool DigestNow, bool PrintWeek, bool ClearState, bool Verbose, bool ShowHelp)
{
    /// <summary>Parses process arguments. Only switches are supported.</summary>
    public static CommandLine Parse(string[] args)
    {
        var once = false;
        var dryRun = false;
        var test = false;
                var digest = false;
        var printWeek = false;
        var clear = false;
        var verbose = false;
        var help = false;

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--once": once = true; break;
                case "--dry-run": dryRun = true; break;
                case "--test": test = true; break;
                case "--digest-now": digest = true; break;
                case "--print-week": printWeek = true; break;
                case "--clear-state": clear = true; break;
                case "--verbose" or "-v": verbose = true; break;
                case "--help" or "-h" or "/?": help = true; break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {arg}");
                    help = true;
                    break;
            }
        }

        return new CommandLine(once, dryRun, test, digest, printWeek, clear, verbose, help);
    }

    /// <summary>Usage text printed for --help.</summary>
    public const string HelpText = """
        Calendar MCP reminders host

        Usage:
          CalendarMcp.Reminders [options]

        Options:
          --once         Run a single digest/reminder pass and exit
          --dry-run      Log what would be sent without sending it
          --test         Send one test message and exit
          --digest-now   Send today's digest now without recording state
          --print-week   Print the coming week to the console and exit
          --clear-state  Delete the reminder state file and exit
          --verbose      Debug logging
          --help         Show this text

        Configuration comes from the Notifications section of appsettings.json,
        resolved through CALENDAR_MCP_CONFIG or the default data directory.
        """;
}

/// <summary>Time zone and week length shared by the formatter and the services.</summary>
internal sealed record TimeZoneInfoSettings(TimeZoneInfo Zone, int WeekDays);