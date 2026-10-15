namespace CalendarMcp.Reminders;

/// <summary>
/// Turns an inbound chat message into a period request. Commands are ASCII only —
/// Telegram requires that — so the Russian phrases are matched as ordinary text.
/// </summary>
public static class CommandInterpreter
{
    /// <summary>
    /// Parses a message. Returns false for anything that is not a recognised
    /// request, including empty text and messages addressed to somebody else.
    /// </summary>
    /// <param name="text">Raw message text.</param>
    /// <param name="period">Period the message asks for.</param>
    public static bool TryParse(string? text, out MeetingPeriod period)
    {
        period = MeetingPeriod.Today;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = Normalize(text);
        if (normalized.Length == 0) return false;

        // Commands win over keyword matching so /week is unambiguous.
        switch (normalized)
        {
            case "/today" or "/now":
                period = MeetingPeriod.Today;
                return true;
            case "/tomorrow":
                period = MeetingPeriod.Tomorrow;
                return true;
            case "/week":
                period = MeetingPeriod.Week;
                return true;
        }

        if (Contains(normalized, ["завтра", "завтрашн"]))
        {
            period = MeetingPeriod.Tomorrow;
            return true;
        }

        if (Contains(normalized, ["недел", "неделе", "неделю", "неделя"]))
        {
            period = MeetingPeriod.Week;
            return true;
        }

        if (Contains(normalized, ["сегодня", "сегодняшн", "на сегодня", "сейчас"]))
        {
            period = MeetingPeriod.Today;
            return true;
        }

        return false;
    }

    /// <summary>Text shown for an unrecognised message so the user learns the syntax.</summary>
    public static string HelpText =>
        "Команды:\n" +
        "/today — встречи на сегодня\n" +
        "/tomorrow — встречи на завтра\n" +
        "/week — встречи на неделю\n" +
        "\nТо же самое обычным текстом: «встречи завтра», «что на неделе».";

    /// <summary>Lowercases and trims, leaving text otherwise untouched.</summary>
    private static string Normalize(string text) => text.Trim().ToLowerInvariant();

    /// <summary>True when the normalized text contains any of the given fragments.</summary>
    private static bool Contains(string text, string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            if (text.Contains(fragment, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}