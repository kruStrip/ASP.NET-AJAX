using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using MuzLoto.Web.Services.Messaging;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>
/// TelegramBotChannel against a scripted Telegram Bot API: the chat, the forum topic, the caption and the receipt
/// file it uploads. No network, no database.
/// </summary>
public sealed class TelegramBotChannelTests
{
    private sealed class ScriptedFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ScriptedHttp : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];
        private readonly Queue<HttpResponseMessage> responses = [];
        public void Enqueue(HttpResponseMessage response) => responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (responses.Count == 0) throw new HttpRequestException("no response scripted");
            return responses.Dequeue();
        }
    }

    /// <summary>A receipt on disk without the storage service or a database.</summary>
    private sealed class FakeReceipts(string? path, string? contentType = "image/png") : IReceiptFiles
    {
        public (string Path, string ContentType)? OpenReceipt(Booking booking) =>
            path is null ? null : (path, contentType!);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static TelegramBotChannel Channel(ScriptedHttp http, IConfiguration config, IReceiptFiles? files = null) =>
        new(new ScriptedFactory(http), config, files ?? new FakeReceipts(null), Links(config));

    /// <summary>Signed receipt links as the application builds them, from a throwaway key ring.</summary>
    private static ReceiptLinks Links(IConfiguration config) => new(
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), "opencode", "dp", Guid.NewGuid().ToString("N")))),
        config);

    private static readonly IConfiguration Ready = Config(
        ("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"), ("Telegram:ManagerTopicId", "77"));

    private static readonly IConfiguration Relay = Config(
        ("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"), ("Telegram:ManagerTopicId", "77"),
        ("Telegram:ApiBaseUrl", "https://relay.example.workers.dev/"), ("Telegram:RelaySecret", "s3cret"));

    private static Booking Booking() => new() { CustomerName = "Вика", Phone = "+79140000000" };
    private static OutgoingMessage Message(string text = "Новая оплата ждёт подтверждения") => new() { Booking = Booking(), Text = text };
    private static OutgoingMessage ReceiptMessage(string key = "receipts/abc-20261103152308507.jpg", string text = "Номер клиента: +79140000000") =>
        new() { Booking = new Booking { Id = Guid.NewGuid(), CustomerName = "Вика", Phone = "+79140000000", ReceiptKey = key },
                Text = text, Kind = OutgoingMessageKind.ReceiptNotification };
    private static OutgoingMessage ContactMessage(string text = "Связаться с клиентом") =>
        new() { Booking = Booking(), Text = text, Kind = OutgoingMessageKind.ContactRequest };
    private static OutgoingMessage StaffMessage(OutgoingMessageKind kind, string text = "Изменение брони") =>
        new()
        {
            Booking = new Booking { Id = Guid.NewGuid(), Phone = "+79140000000", ReceiptKey = "receipts/abc.png" },
            Text = text, Kind = kind
        };
    private static HttpResponseMessage Ok(string json = """{"ok":true}""") =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string TempFile(string name, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "opencode", Guid.NewGuid().ToString("N") + name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task PhotoReceipt_IsUploadedToTheTopic_WithTheCaption()
    {
        var http = new ScriptedHttp();
        http.Enqueue(Ok());
        var receipts = new FakeReceipts(TempFile(".png", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]));

        var outcome = await Channel(http, Ready, receipts)
            .SendAsync(ReceiptMessage("receipts/abc-20261103152308507.png", "Номер клиента: +79140000000"), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Sent, outcome.Status);

        var (request, body) = Assert.Single(http.Seen);
        Assert.Equal("https://api.telegram.org/bot123:ABC/sendPhoto", request.RequestUri!.ToString());
        foreach (var part in new[] { "name=chat_id", "-100123456", "name=message_thread_id", "77",
                     "name=caption", "Номер клиента: +79140000000", "name=photo; filename=" })
            Assert.Contains(part, body);
        Assert.DoesNotContain("name=text", body);
    }

    [Fact]
    public async Task PdfReceipt_IsSentAsADocument_AndMissingReceiptAsText()
    {
        var pdf = new ScriptedHttp();
        pdf.Enqueue(Ok());
        await Channel(pdf, Ready, new FakeReceipts(TempFile(".pdf", "%PDF-1.4"u8.ToArray()), "application/pdf"))
            .SendAsync(ReceiptMessage("receipts/abc-20261103152308507.pdf"), CancellationToken.None);
        Assert.Contains("sendDocument", pdf.Seen.Single().Request.RequestUri!.ToString());

        // The booking has a receipt key but the file is gone: the manager still gets the text.
        var text = new ScriptedHttp();
        text.Enqueue(Ok());
        await Channel(text, Ready).SendAsync(ReceiptMessage("receipts/gone-20261103152308507.png", "текст"), CancellationToken.None);
        var (request, body) = text.Seen.Single();
        Assert.EndsWith("/sendMessage", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("-100123456", json.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal(77, json.RootElement.GetProperty("message_thread_id").GetInt64());
        Assert.Equal("текст", json.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task WithoutTokenOrChat_NotificationFails_WithoutCallingTelegram()
    {
        var http = new ScriptedHttp();
        var channel = Channel(http, Config(("Telegram:BotToken", "123:ABC")));
        Assert.False(await channel.IsReadyAsync(CancellationToken.None));
        var outcome = await channel.SendAsync(Message(), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Failed, outcome.Status);
        Assert.Contains("TELEGRAM_MANAGER_CHAT_ID", outcome.Error);
        Assert.Empty(http.Seen);
    }

    [Fact]
    public async Task ContactRequest_GoesToItsOwnTopic_AndNeedsOne()
    {
        var withTopic = Config(("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"),
            ("Telegram:ManagerTopicId", "77"), ("Telegram:ContactTopicId", "88"));
        var http = new ScriptedHttp();
        http.Enqueue(Ok());
        Assert.Equal(OutgoingMessageStatus.Sent,
            (await Channel(http, withTopic).SendAsync(ContactMessage(), CancellationToken.None)).Status);
        using (var json = JsonDocument.Parse(http.Seen.Single().Body))
            Assert.Equal(88, json.RootElement.GetProperty("message_thread_id").GetInt64());

        var noTopic = new ScriptedHttp();   // the «Подтвердить игру» topic is not a fallback for «Связаться с клиентом»
        var outcome = await Channel(noTopic, Ready).SendAsync(ContactMessage(), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Failed, outcome.Status);
        Assert.Contains("TELEGRAM_CONTACT_TOPIC_ID", outcome.Error);
        Assert.Empty(noTopic.Seen);
    }

    /// <summary>
    /// «Связаться с клиентом» is sent as HTML with the customer's block in &lt;pre&gt;: Telegram then shows a «Copy»
    /// button, and a manager copies the text with one tap instead of selecting it by hand on a phone.
    /// </summary>
    [Fact]
    public async Task ContactRequest_IsSentAsHtml_WithACopyableBlock()
    {
        var withTopic = Config(("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"),
            ("Telegram:ManagerTopicId", "77"), ("Telegram:ContactTopicId", "88"));
        var http = new ScriptedHttp();
        http.Enqueue(Ok());
        var text = "Связаться с клиентом\n\nTelegram: https://t.me/team_msg\n\nДля отправки информации клиенту:\n"
                   + "__________________________\nЗдравствуйте!\nИтого: 1\u00A0400 ₽";
        await Channel(http, withTopic).SendAsync(ContactMessage(text), CancellationToken.None);

        using var json = JsonDocument.Parse(http.Seen.Single().Body);
        Assert.Equal("HTML", json.RootElement.GetProperty("parse_mode").GetString());
        var sent = json.RootElement.GetProperty("text").GetString()!;
        Assert.StartsWith("Связаться с клиентом", sent);
        Assert.Contains("<pre>Здравствуйте!\nИтого: 1\u00A0400 ₽</pre>", sent);
        Assert.DoesNotContain("https://t.me/team_msg</pre>", sent);   // links stay outside the copyable block
    }

    /// <summary>A receipt attached to the same notification: the HTML block rides along as the caption.</summary>
    [Fact]
    public async Task ContactRequestWithAReceipt_KeepsTheHtmlCaption_ThroughARelayAndOnUpload()
    {
        var withTopic = Config(("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"),
            ("Telegram:ContactTopicId", "88"), ("Telegram:ApiBaseUrl", "https://relay.example.workers.dev"),
            ("Telegram:RelaySecret", "s3cret"));
        var message = new OutgoingMessage
        {
            Booking = new Booking { Id = Guid.NewGuid(), Phone = "+79140000000", ReceiptKey = "receipts/abc.png" },
            Text = "Связаться с клиентом\n__________________________\nЗдравствуйте!", Kind = OutgoingMessageKind.ContactRequest
        };

        var relay = new ScriptedHttp();
        relay.Enqueue(Ok());
        await Channel(relay, withTopic).SendAsync(message, CancellationToken.None);
        using (var json = JsonDocument.Parse(relay.Seen.Single().Body))
        {
            Assert.Equal("HTML", json.RootElement.GetProperty("parseMode").GetString());
            Assert.Equal("<pre>Здравствуйте!</pre>", json.RootElement.GetProperty("caption").GetString()!.Split('\n')[^1]);
        }

        // Without a relay the file is uploaded as multipart: the parse mode must be a form field there too.
        var upload = new ScriptedHttp();
        upload.Enqueue(Ok());
        var direct = Config(("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"),
            ("Telegram:ContactTopicId", "88"));
        await Channel(upload, direct, new FakeReceipts(TempFile(".png", [0x89, 0x50, 0x4E, 0x47])))
            .SendAsync(message, CancellationToken.None);
        var body = upload.Seen.Single().Body;
        Assert.Contains("name=parse_mode", body);
        Assert.Contains("HTML", body);
        Assert.Contains("<pre>", body);   // the caption keeps its HTML; Telegram parses it with parse_mode=HTML
    }

    /// <summary>
    /// «Изменения в бронях» and «Отмена игр» are staff notices sent as HTML (so the phone is tappable to copy) to
    /// their own topics; the booking's receipt is deliberately not attached to them (2026-11-04).
    /// </summary>
    [Fact]
    public async Task StaffTopics_ReceiveChangesAndCancellations_WithoutTheReceipt()
    {
        var config = Config(("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"),
            ("Telegram:ManagerTopicId", "77"), ("Telegram:ChangesTopicId", "99"), ("Telegram:CancelTopicId", "100"));
        var http = new ScriptedHttp();
        http.Enqueue(Ok());
        http.Enqueue(Ok());
        var channel = Channel(http, config, new FakeReceipts(TempFile(".png", [0x89, 0x50, 0x4E, 0x47])));
        const string text = "Изменение брони\n\nТелефон: 79140000000";

        Assert.Equal(OutgoingMessageStatus.Sent,
            (await channel.SendAsync(StaffMessage(OutgoingMessageKind.BookingChanged, text), CancellationToken.None)).Status);
        Assert.Equal(OutgoingMessageStatus.Sent,
            (await channel.SendAsync(StaffMessage(OutgoingMessageKind.BookingCancelled, text), CancellationToken.None)).Status);

        Assert.All(http.Seen, x => Assert.EndsWith("/sendMessage", x.Request.RequestUri!.ToString()));
        using var changed = JsonDocument.Parse(http.Seen[0].Body);
        Assert.Equal(99, changed.RootElement.GetProperty("message_thread_id").GetInt64());
        Assert.Equal("HTML", changed.RootElement.GetProperty("parse_mode").GetString());
        Assert.Contains("Телефон: <code>79140000000</code>", changed.RootElement.GetProperty("text").GetString());
        using var cancelled = JsonDocument.Parse(http.Seen[1].Body);
        Assert.Equal(100, cancelled.RootElement.GetProperty("message_thread_id").GetInt64());
    }

    /// <summary>Until the topic ids are set, the notices land in the group's General topic instead of waiting.</summary>
    [Fact]
    public async Task StaffTopics_WithoutTheirIds_FallBackToTheGeneralTopic()
    {
        var http = new ScriptedHttp();
        http.Enqueue(Ok());
        await Channel(http, Ready).SendAsync(StaffMessage(OutgoingMessageKind.BookingChanged), CancellationToken.None);
        using var json = JsonDocument.Parse(http.Seen.Single().Body);
        Assert.False(json.RootElement.TryGetProperty("message_thread_id", out _));
    }

    [Fact]
    public async Task TelegramErrors_AreExplained()    {
        var unauthorized = new ScriptedHttp();
        unauthorized.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.Contains("TELEGRAM_BOT_TOKEN", (await Channel(unauthorized, Ready).SendAsync(Message(), CancellationToken.None)).Error);

        var noRights = new ScriptedHttp();
        noRights.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden));
        Assert.Contains("добавьте его в группу", (await Channel(noRights, Ready).SendAsync(Message(), CancellationToken.None)).Error);

        var flood = new ScriptedHttp();
        flood.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = new StringContent("""{"ok":false,"description":"Too Many Requests","parameters":{"retry_after":90}}""", Encoding.UTF8, "application/json") });
        var outcome = await Channel(flood, Ready).SendAsync(Message(), CancellationToken.None);
        Assert.Equal((OutgoingMessageStatus.Failed, TimeSpan.FromSeconds(90)), (outcome.Status, outcome.RetryAfter));
    }

    /// <summary>
    /// The production server cannot reach api.telegram.org (Telegram is blocked for the hosting), so the calls go
    /// through a small relay: Telegram:ApiBaseUrl replaces the host and every call carries the shared secret.
    /// </summary>
    [Fact]
    public async Task Relay_CarriesTheCallsAndTheSharedSecret()
    {
        var http = new ScriptedHttp();
        http.Enqueue(Ok());

        var outcome = await Channel(http, Relay).SendAsync(Message("текст"), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Sent, outcome.Status);
        var (request, _) = Assert.Single(http.Seen);
        Assert.Equal("https://relay.example.workers.dev/bot123:ABC/sendMessage", request.RequestUri!.ToString());
        Assert.Equal("s3cret", Assert.Single(request.Headers.GetValues("X-Relay-Secret")));
    }

    /// <summary>
    /// The provider's filtering drops any request body over ~10 KB sent to the relay's *.workers.dev host (measured
    /// on the production server 2026-11-03), so a receipt is never uploaded: the site passes a signed link and the
    /// relay downloads the file itself. The request must stay tiny.
    /// </summary>
    [Fact]
    public async Task Relay_GetsASmallRequestWithTheSignedReceiptLink()
    {
        var http = new ScriptedHttp();
        http.Enqueue(Ok());
        var message = ReceiptMessage();

        // No file on disk: through a relay the site does not touch the file at all.
        var outcome = await Channel(http, Relay).SendAsync(message, CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Sent, outcome.Status);

        var (request, body) = Assert.Single(http.Seen);
        Assert.Equal("https://relay.example.workers.dev/send-file", request.RequestUri!.ToString());
        Assert.Equal("s3cret", Assert.Single(request.Headers.GetValues("X-Relay-Secret")));
        using var json = JsonDocument.Parse(body);
        Assert.Equal("123:ABC", json.RootElement.GetProperty("token").GetString());
        Assert.Equal("-100123456", json.RootElement.GetProperty("chatId").GetString());
        Assert.Equal("77", json.RootElement.GetProperty("topicId").GetString());
        Assert.Equal("abc-20261103152308507.jpg", json.RootElement.GetProperty("fileName").GetString());
        Assert.Equal("Номер клиента: +79140000000", json.RootElement.GetProperty("caption").GetString());
        Assert.StartsWith($"https://muzloto.b-o-c-a.ru/receipts/{message.Booking.Id:N}/abc-20261103152308507.jpg?t=",
            json.RootElement.GetProperty("fileUrl").GetString());
        Assert.True(body.Length < 2048, $"the relay request must stay small, it is {body.Length} bytes");
    }

    [Fact]
    public async Task WithoutAReceipt_EvenThroughARelayTheMessageIsPlainText()
    {
        var http = new ScriptedHttp();
        http.Enqueue(Ok());
        await Channel(http, Relay).SendAsync(Message("текст"), CancellationToken.None);
        Assert.EndsWith("/bot123:ABC/sendMessage", http.Seen.Single().Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task OldRelayWithoutSendFile_SaysWhatToUpdate()
    {
        var http = new ScriptedHttp();
        http.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") });
        var outcome = await Channel(http, Relay).SendAsync(ReceiptMessage(), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Failed, outcome.Status);
        Assert.Contains("worker.js", outcome.Error);
        Assert.DoesNotContain("добавьте его в группу", outcome.Error);
    }

    [Fact]
    public async Task RelayFileErrors_AreShownAsTheRelaysReason()
    {
        var http = new ScriptedHttp();
        http.Enqueue(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("""{"ok":false,"error_code":502,"description":"Релей: сайт ответил 404 на запрос файла."}""",
                Encoding.UTF8, "application/json")
        });
        var outcome = await Channel(http, Relay).SendAsync(ReceiptMessage(), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Failed, outcome.Status);
        Assert.Contains("сайт ответил 404", outcome.Error);
    }

    [Fact]
    public async Task RelayRefusal_IsExplainedInsteadOfBlamingTheBotToken()
    {
        var http = new ScriptedHttp();
        http.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("forbidden") });
        var relayed = Config(("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"),
            ("Telegram:ApiBaseUrl", "https://relay.example.workers.dev"), ("Telegram:RelaySecret", "wrong"));

        var outcome = await Channel(http, relayed).SendAsync(Message(), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Failed, outcome.Status);
        Assert.Contains("TELEGRAM_RELAY_SECRET", outcome.Error);
        Assert.DoesNotContain("добавьте его в группу", outcome.Error);
    }
}
