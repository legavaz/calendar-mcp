using CalendarMcp.Core.Models;
using CalendarMcp.Core.Services;

namespace CalendarMcp.Reminders;

/// <summary>Supplies meetings for the account whose calendar is being watched.</summary>
public interface IMeetingSource
{
    /// <summary>Mailbox address used to recognise the owner's own meetings; null when unknown.</summary>
    string? OwnEmail { get; }

    /// <summary>Resolves the configured account and caches its identity.</summary>
    Task<AccountInfo?> ResolveAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches meetings in <c>[from, until)</c>.</summary>
    Task<IReadOnlyList<CalendarEvent>> GetMeetingsAsync(DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the mailbox owner's calendar through the provider the account already
/// uses, so reminders share the EWS implementation and its credential handling
/// with the MCP server instead of re-implementing the protocol.
/// </summary>
public sealed class MeetingSource : IMeetingSource
{
    private readonly IAccountRegistry _accounts;
    private readonly IProviderServiceFactory _providers;
    private readonly ReminderOptions _options;
    private readonly ILogger<MeetingSource> _logger;

    public MeetingSource(
        IAccountRegistry accounts,
        IProviderServiceFactory providers,
        ReminderOptions options,
        ILogger<MeetingSource> logger)
    {
        _accounts = accounts;
        _providers = providers;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Mailbox address the account authenticates as, used to tell the owner's own
    /// meetings from everyone else's. Null when the provider does not expose one.
    /// </summary>
    public string? OwnEmail { get; private set; }

    /// <summary>Resolves the configured account once, caching its identity and address.</summary>
    public async Task<AccountInfo?> ResolveAccountAsync(CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetAccountAsync(_options.AccountId);
        if (account is null)
        {
            _logger.LogError(
                "Account {AccountId} is not configured; check Notifications:AccountId in appsettings.json",
                _options.AccountId);
            return null;
        }

        OwnEmail = account.ProviderConfig.TryGetValue("username", out var username)
            && !string.IsNullOrWhiteSpace(username)
                ? username.Trim()
                : null;

        return account;
    }

    /// <summary>
    /// Fetches meetings in <c>[from, until)</c>. Both bounds are converted to UTC
    /// because the provider layer works in UTC regardless of display zone.
    /// </summary>
    /// <param name="from">Inclusive start of the range.</param>
    /// <param name="until">Exclusive end of the range.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<CalendarEvent>> GetMeetingsAsync(
        DateTimeOffset from,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        var account = await ResolveAccountAsync(cancellationToken);
        if (account is null) return [];

        try
        {
            var provider = _providers.GetProvider(account.Provider);
            var meetings = await provider.GetCalendarEventsAsync(
                account.Id,
                calendarId: null,
                startDate: from.UtcDateTime,
                endDate: until.UtcDateTime,
                count: 500,
                cancellationToken: cancellationToken);

            return meetings?.ToList() ?? [];
        }
        catch (Exception ex)
        {
            // A calendar fetch failure must not stop the host; the next pass retries.
            _logger.LogError(ex, "Could not read the calendar for account {AccountId}", account.Id);
            return [];
        }
    }
}