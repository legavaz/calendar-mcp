using System.Net;
using System.Text;
using CalendarMcp.Reminders.Notification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalendarMcp.Tests.Reminders;

/// <summary>
/// Covers Telegram payload parsing and delivery decisions without touching the
/// network: a fake handler stands in for the Bot API.
/// </summary>
[TestClass]
public class TelegramClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body = "{\"ok\":true}") : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? string.Empty);
            var content = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(content);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    /// <summary>Reads the JSON body the stub recorded, so assertions compare real values.</summary>
    private static (string chat_id, string text, bool disable_web_page_preview, string? parse_mode) ReadPayload(StubHandler handler)
    {
        using var document = System.Text.Json.JsonDocument.Parse(handler.Requests[1]);
        var root = document.RootElement;
        return (
            root.GetProperty("chat_id").GetString() ?? string.Empty,
            root.GetProperty("text").GetString() ?? string.Empty,
            root.GetProperty("disable_web_page_preview").GetBoolean(),
            root.TryGetProperty("parse_mode", out var mode) ? mode.GetString() : null);
    }

    [TestMethod]
    public void UpdatesUri_CarriesOffsetAndLongPollTimeout()
    {
        var uri = TelegramClient.UpdatesUri("TOKEN", 42, 50).ToString();

        StringAssert.Contains(uri, "botTOKEN/getUpdates");
        StringAssert.Contains(uri, "offset=42");
        StringAssert.Contains(uri, "timeout=50");
        StringAssert.Contains(uri, "allowed_updates");
    }

    [TestMethod]
    public void ParseUpdates_ExtractsIdChatAndText()
    {
        const string json = """
        {"ok":true,"result":[
          {"update_id":7,"message":{"message_id":1,"chat":{"id":123},"text":"/week"}}
        ]}
        """;

        var updates = TelegramClient.ParseUpdates(json);

        Assert.AreEqual(1, updates.Count);
        Assert.AreEqual(7, updates[0].UpdateId);
        Assert.AreEqual("123", updates[0].ChatId);
        Assert.AreEqual("/week", updates[0].Text);
    }

    [TestMethod]
    public void ParseUpdates_SkipsNonMessageUpdatesAndEmptyText()
    {
        const string json = """
        {"ok":true,"result":[
          {"update_id":1,"channel_post":{"chat":{"id":1},"text":"hi"}},
          {"update_id":2,"message":{"chat":{"id":1},"text":"   "}},
          {"update_id":3,"message":{"chat":{"id":9},"text":"/today"}}
        ]}
        """;

        var updates = TelegramClient.ParseUpdates(json);

        Assert.AreEqual(1, updates.Count);
        Assert.AreEqual(3, updates[0].UpdateId);
    }

    [TestMethod]
    public void ParseUpdates_MalformedJson_ReturnsEmpty()
    {
        Assert.AreEqual(0, TelegramClient.ParseUpdates("not json").Count);
        Assert.AreEqual(0, TelegramClient.ParseUpdates("{\"ok\":false}").Count);
    }

    [TestMethod]
    public async Task SendMessage_SendsPlainTextWithoutParseMode()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = new TelegramClient(new HttpClient(handler), NullLogger<TelegramClient>.Instance);

        var ok = await client.SendMessageAsync("TOKEN", "123", "Через 15 мин");

        Assert.IsTrue(ok);
        StringAssert.Contains(handler.Requests[0], "/sendMessage");

        var payload = ReadPayload(handler);
        Assert.AreEqual("123", payload.chat_id, "Telegram rejects the camelCase name chatId");
        Assert.AreEqual("Через 15 мин", payload.text);
        Assert.IsTrue(payload.disable_web_page_preview);
        Assert.IsNull(payload.parse_mode, "plain text avoids escaping issues");
    }

    [TestMethod]
    public async Task SendMessage_KeepsAngleBracketsVerbatim()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = new TelegramClient(new HttpClient(handler), NullLogger<TelegramClient>.Instance);

        await client.SendMessageAsync("TOKEN", "123", "RE: <Договор> & проект");

        Assert.AreEqual("RE: <Договор> & проект", ReadPayload(handler).text);
    }

    [TestMethod]
    public async Task SendMessage_ClientErrorIsReportedAsFailureWithoutThrowing()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, "{\"ok\":false}");
        var client = new TelegramClient(new HttpClient(handler), NullLogger<TelegramClient>.Instance);

        var ok = await client.SendMessageAsync("TOKEN", "123", "text");

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task GetUpdates_ReturnsEmptyOnErrorResponse()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized);
        var client = new TelegramClient(new HttpClient(handler), NullLogger<TelegramClient>.Instance);

        var updates = await client.GetUpdatesAsync("TOKEN", 0, 1);

        Assert.AreEqual(0, updates.Count);
    }

    [TestMethod]
    public async Task Notifier_RetriesOnceAfterAFailedSend()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var notifier = new TelegramNotifier(
            new TelegramClient(new HttpClient(handler), NullLogger<TelegramClient>.Instance),
            "TOKEN",
            "123");

        await notifier.SendAsync("ping");

        Assert.AreEqual(2, handler.Requests.Count / 2, "expected one initial attempt and one retry");
    }

    [TestMethod]
    public void NotifierApiBase_UsesTheTokenInTheUrl()
    {
        var uri = TelegramClient.Method("ABC", "getMe");

        Assert.AreEqual("https://api.telegram.org/botABC/getMe", uri.ToString());
    }
}