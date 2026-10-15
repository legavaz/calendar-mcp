using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarMcp.Reminders.Notification;

/// <summary>
/// Thin wrapper over the Telegram Bot API covering the two calls this host needs:
/// sending a message and draining pending commands.
/// </summary>
public sealed class TelegramClient
{
    private readonly HttpClient _http;
    private readonly ILogger<TelegramClient> _logger;

    public TelegramClient(HttpClient http, ILogger<TelegramClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Base address of the Bot API for a token.</summary>
    public static Uri ApiBase(string botToken) => new($"https://api.telegram.org/bot{botToken}/");

    /// <summary>Address of a single Bot API method.</summary>
    public static Uri Method(string botToken, string method) => new(ApiBase(botToken), method);

    /// <summary>
    /// Address of getUpdates. <paramref name="timeout"/> is the server-side
    /// long-poll hold time, so a request blocks instead of returning immediately
    /// and spinning.
    /// </summary>
    public static Uri UpdatesUri(string botToken, int offset, int timeout) =>
        new($"https://api.telegram.org/bot{botToken}/getUpdates?timeout={timeout}&offset={offset}&allowed_updates=%5B%22message%22%5D");

    /// <summary>
    /// Sends one plain-text message. No parse mode is set, so meeting subjects
    /// containing &lt;, &gt; or &amp; are delivered verbatim instead of needing escaping.
    /// </summary>
    /// <returns>True when Telegram accepted the message.</returns>
    public async Task<bool> SendMessageAsync(string botToken, string chatId, string text, CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = JsonContent.Create(new SendMessagePayload
            {
                ChatId = chatId,
                Text = text,
                DisableWebPagePreview = true
            });

            using var response = await _http.PostAsync(Method(botToken, "sendMessage"), content, cancellationToken);

            if (response.IsSuccessStatusCode) return true;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // A 4xx is a defect in our request; repeating it cannot help.
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                _logger.LogError("Telegram rejected sendMessage with {Status}: {Body}", (int)response.StatusCode, body);
                return false;
            }

            _logger.LogWarning("Telegram sendMessage returned {Status}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Telegram sendMessage transport failure");
            return false;
        }
    }

    /// <summary>
    /// Fetches pending updates starting at <paramref name="offset"/>. Returns an
    /// empty list when Telegram has nothing, and treats an error response as
    /// "nothing yet" so a temporary outage does not kill the command loop.
    /// </summary>
    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string botToken, int offset, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdatesUri(botToken, offset, timeoutSeconds));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Telegram getUpdates returned {Status}", (int)response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseUpdates(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Telegram getUpdates transport failure");
            return [];
        }
    }

    /// <summary>
    /// Extracts the text of message updates from a getUpdates payload. Non-message
    /// updates and messages without text are skipped.
    /// </summary>
    public static IReadOnlyList<TelegramUpdate> ParseUpdates(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var updates = new List<TelegramUpdate>();
            foreach (var item in result.EnumerateArray())
            {
                if (!item.TryGetProperty("message", out var message)) continue;
                if (!message.TryGetProperty("text", out var textElement)) continue;

                var text = textElement.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;

                string chatId = string.Empty;
                if (message.TryGetProperty("chat", out var chat)
                    && chat.TryGetProperty("id", out var id))
                {
                    chatId = id.ValueKind == JsonValueKind.Number
                        ? id.GetInt64().ToString()
                        : id.GetString() ?? string.Empty;
                }

                var updateId = item.TryGetProperty("update_id", out var idElement) ? idElement.GetInt32() : 0;
                updates.Add(new TelegramUpdate(updateId, chatId, text));
            }

            return updates;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class SendMessagePayload
    {
        [JsonPropertyName("chat_id")]
        public string ChatId { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("disable_web_page_preview")]
        public bool DisableWebPagePreview { get; set; }
    }
}

/// <summary>One inbound message from the bot's chat.</summary>
/// <param name="UpdateId">Telegram update id, used as the acknowledgement offset.</param>
/// <param name="ChatId">Chat the message came from; only the configured chat is answered.</param>
/// <param name="Text">Raw message text.</param>
public readonly record struct TelegramUpdate(int UpdateId, string ChatId, string Text);

/// <summary>Adapter that satisfies <see cref="INotifier"/> over <see cref="TelegramClient"/>.</summary>
public sealed class TelegramNotifier : INotifier
{
    private readonly TelegramClient _client;
    private readonly string _botToken;
    private readonly string _chatId;

    public TelegramNotifier(TelegramClient client, string botToken, string chatId)
    {
        _client = client;
        _botToken = botToken;
        _chatId = chatId;
    }

    /// <summary>One retry: a single dropped request should not cost an alert.</summary>
    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (await _client.SendMessageAsync(_botToken, _chatId, message, cancellationToken)) return;

        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        await _client.SendMessageAsync(_botToken, _chatId, message, cancellationToken);
    }
}