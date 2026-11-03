using System.Net;
using System.Text;
using System.Text.Json;
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
        new(new ScriptedFactory(http), config, files ?? new FakeReceipts(null));

    private static readonly IConfiguration Ready = Config(
        ("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"), ("Telegram:ManagerTopicId", "77"));

    private static Booking Booking() => new() { CustomerName = "Вика", Phone = "+79140000000" };
    private static OutgoingMessage Message(string text = "Новая оплата ждёт подтверждения") => new() { Booking = Booking(), Text = text };
    private static OutgoingMessage ContactMessage(string text = "Связаться с клиентом") =>
        new() { Booking = Booking(), Text = text, Kind = OutgoingMessageKind.ContactRequest };
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

        var outcome = await Channel(http, Ready, receipts).SendAsync(Message("Номер клиента: +79140000000"), CancellationToken.None);
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
            .SendAsync(Message(), CancellationToken.None);
        Assert.Contains("sendDocument", pdf.Seen.Single().Request.RequestUri!.ToString());

        var text = new ScriptedHttp();
        text.Enqueue(Ok());
        await Channel(text, Ready).SendAsync(Message("текст"), CancellationToken.None);
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

    [Fact]
    public async Task TelegramErrors_AreExplained()
    {
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
        var relayed = Config(("Telegram:BotToken", "123:ABC"), ("Telegram:ManagerChatId", "-100123456"),
            ("Telegram:ManagerTopicId", "77"), ("Telegram:ApiBaseUrl", "https://relay.example.workers.dev/"),
            ("Telegram:RelaySecret", "s3cret"));

        var outcome = await Channel(http, relayed).SendAsync(Message("текст"), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Sent, outcome.Status);
        var (request, _) = Assert.Single(http.Seen);
        Assert.Equal("https://relay.example.workers.dev/bot123:ABC/sendMessage", request.RequestUri!.ToString());
        Assert.Equal("s3cret", Assert.Single(request.Headers.GetValues("X-Relay-Secret")));

        var photo = new ScriptedHttp();   // the file upload goes the same way
        photo.Enqueue(Ok());
        await Channel(photo, relayed, new FakeReceipts(TempFile(".png", [0x89, 0x50, 0x4E, 0x47, 1])))
            .SendAsync(Message(), CancellationToken.None);
        Assert.Equal("https://relay.example.workers.dev/bot123:ABC/sendPhoto",
            photo.Seen.Single().Request.RequestUri!.ToString());
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
