using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services.Messaging;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>
/// WazzupChannel against a scripted HTTP backend: the request bodies it sends to api.wazzup24.com, how it picks
/// the channel to send from, and how Wazzup's answers map to the message journal. No network, no database.
/// </summary>
public sealed class WazzupChannelTests
{
    private sealed class ScriptedFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.wazzup24.com/") };
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

    private static IConfiguration Config(params (string Key, string? Value)[] values)
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value));
        return builder.Build();
    }

    private static WazzupChannel Channel(ScriptedHttp http, IConfiguration config, MessageChannel channel) =>
        new(new ScriptedFactory(http), config, channel);

    private static Booking Booking(string phone = "+79140000000", string? telegram = "@team") => new()
    {
        TeamName = "МЯУ", CustomerName = "Вика", Phone = phone, Telegram = telegram, Quantity = 2
    };

    private static OutgoingMessage Message(Booking booking, string text = "", string? contentUri = null) =>
        new() { Booking = booking, Text = text, ContentUri = contentUri };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Channels(params string[] channels) =>
        Json(HttpStatusCode.OK, "[" + string.Join(",", channels) + "]");

    private static string ChannelJson(string id, string transport, string? plain = null, string state = "active") =>
        $$"""{"channelId":"{{id}}","transport":"{{transport}}","plainId":{{(plain is null ? "null" : $"\"{plain}\"")}},"state":"{{state}}"}""";

    /// <summary>Fresh every time: the channel disposes the response after reading it.</summary>
    private static HttpResponseMessage Accepted => new(HttpStatusCode.Created);

    [Fact]
    public async Task WhatsApp_SendsByPhoneNumber_ThroughTheMatchingChannel()
    {
        var http = new ScriptedHttp();
        http.Enqueue(Channels(ChannelJson("wa-1", "whatsapp", "79865784457")));
        http.Enqueue(Accepted);
        var channel = Channel(http, Config(("Wazzup:ApiKey", "key")), MessageChannel.WhatsApp);

        Assert.True(await channel.IsReadyAsync(CancellationToken.None));
        var outcome = await channel.SendAsync(Message(Booking(), "текст"), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Sent, outcome.Status);

        var (request, body) = http.Seen.Last();
        Assert.Equal("https://api.wazzup24.com/v3/message", request.RequestUri!.ToString());
        using var json = System.Text.Json.JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("wa-1", root.GetProperty("channelId").GetString());
        Assert.Equal("whatsapp", root.GetProperty("chatType").GetString());
        Assert.Equal("79140000000", root.GetProperty("chatId").GetString());   // +7 914 000-00-00 → digits only
        Assert.Equal("текст", root.GetProperty("text").GetString());
        Assert.Equal(4, root.EnumerateObject().Count());                        // nothing else is sent
    }

    [Fact]
    public async Task ImageMessage_IsSentAsContentUri_WithoutText()
    {
        var http = new ScriptedHttp();
        http.Enqueue(Channels(ChannelJson("tg-1", "tgapi")));
        http.Enqueue(Accepted);
        var channel = Channel(http, Config(("Wazzup:ApiKey", "key")), MessageChannel.Telegram);

        var message = Message(Booking(), contentUri: "https://muzloto.example/oplata/qr?v=7");
        Assert.Equal(OutgoingMessageStatus.Sent, (await channel.SendAsync(message, CancellationToken.None)).Status);

        using var json = System.Text.Json.JsonDocument.Parse(http.Seen.Last().Body);
        var root = json.RootElement;
        Assert.Equal("https://muzloto.example/oplata/qr?v=7", root.GetProperty("contentUri").GetString());
        Assert.False(root.TryGetProperty("text", out _));   // Wazzup cannot send a file and text together
    }

    [Fact]
    public async Task Telegram_SendsByNickname_OrByPhoneWhenThereIsNone()
    {
        var byNick = new ScriptedHttp();
        byNick.Enqueue(Channels(ChannelJson("tg-1", "tgapi", "@company")));
        byNick.Enqueue(Accepted);
        var channel = Channel(byNick, Config(("Wazzup:ApiKey", "key")), MessageChannel.Telegram);
        Assert.Equal(OutgoingMessageStatus.Sent, (await channel.SendAsync(Message(Booking(), "привет"), CancellationToken.None)).Status);
        var body = byNick.Seen.Last().Body;
        Assert.Contains("\"chatType\":\"telegram\"", body);
        Assert.Contains("\"username\":\"team\"", body);       // @team → without the @
        Assert.DoesNotContain("\"phone\"", body);

        var byPhone = new ScriptedHttp();
        byPhone.Enqueue(Channels(ChannelJson("tg-1", "tgapi", "@company")));
        byPhone.Enqueue(Accepted);
        channel = Channel(byPhone, Config(("Wazzup:ApiKey", "key")), MessageChannel.Telegram);
        Assert.Equal(OutgoingMessageStatus.Sent, (await channel.SendAsync(Message(Booking(telegram: null), "привет"), CancellationToken.None)).Status);
        Assert.Contains("\"phone\":\"79140000000\"", byPhone.Seen.Last().Body);
        Assert.DoesNotContain("\"username\"", byPhone.Seen.Last().Body);
    }

    [Fact]
    public async Task PinnedChannelIdWins_AndAMissingOneIsReported()
    {
        var http = new ScriptedHttp();
        http.Enqueue(Channels(
            ChannelJson("wa-1", "whatsapp", "79865784457"),
            ChannelJson("wa-2", "whatsapp", "79990001122", "init")));
        var channel = Channel(http, Config(
            ("Wazzup:ApiKey", "key"), ("Wazzup:WhatsAppChannelId", "wa-2")), MessageChannel.WhatsApp);

        var state = await channel.StateAsync(CancellationToken.None);
        Assert.Equal("wa-2", state.Resolved!.ChannelId);       // the configured id, even though another one is active
        Assert.Equal("init", state.Status);
        Assert.False(await channel.IsReadyAsync(CancellationToken.None));

        var missingHttp = new ScriptedHttp();
        missingHttp.Enqueue(Channels(ChannelJson("wa-1", "whatsapp")));   // only a WhatsApp channel: no Telegram there
        var missing = Channel(missingHttp, Config(
            ("Wazzup:ApiKey", "key"), ("Wazzup:TelegramChannelId", "no-such")), MessageChannel.Telegram);
        var empty = await missing.StateAsync(CancellationToken.None);
        Assert.Equal("NO_CHANNEL", empty.Status);
        Assert.Contains("no-such", empty.Detail);
    }

    [Fact]
    public async Task WithoutAPinnedId_TheOnlyActiveChannelIsPicked_AndAmbiguityIsHinted()
    {
        var http = new ScriptedHttp();
        http.Enqueue(Channels(
            ChannelJson("wa-1", "whatsapp", "79865784457", "init"),
            ChannelJson("wa-2", "whatsapp", "79990001122")));
        var channel = Channel(http, Config(("Wazzup:ApiKey", "key")), MessageChannel.WhatsApp);
        var state = await channel.StateAsync(CancellationToken.None);
        Assert.Equal("wa-2", state.Resolved!.ChannelId);       // the active one of the two
        Assert.Equal("active", state.Status);
        Assert.Contains("несколько каналов", state.Detail);     // and the page hints to pin one via WAZZUP_WHATSAPP_CHANNEL_ID

        // A plain WhatsApp number wins over WhatsApp Business API even when the latter is the only active one.
        var withWaba = new ScriptedHttp();
        withWaba.Enqueue(Channels(
            ChannelJson("waba", "wapi", "79990001122"),
            ChannelJson("plain", "whatsapp", "79865784457", "qridle")));
        state = await Channel(withWaba, Config(("Wazzup:ApiKey", "key")), MessageChannel.WhatsApp).StateAsync(CancellationToken.None);
        Assert.Equal("plain", state.Resolved!.ChannelId);
        Assert.Equal("qridle", state.Status);
    }

    [Fact]
    public async Task AnswersOfWazzup_MapToTheJournalStatuses()
    {
        var key = Config(("Wazzup:ApiKey", "key"));
        var booking = Booking();

        var unauthorized = new ScriptedHttp();
        unauthorized.Enqueue(Channels(ChannelJson("wa-1", "whatsapp")));
        unauthorized.Enqueue(Json(HttpStatusCode.Unauthorized, """{"error":"UNAUTHORIZED"}"""));
        var outcome = await Channel(unauthorized, key, MessageChannel.WhatsApp).SendAsync(Message(booking, "t"), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Failed, outcome.Status);
        Assert.Contains("WAZZUP_API_KEY", outcome.Error);

        var throttled = new ScriptedHttp();
        throttled.Enqueue(Channels(ChannelJson("wa-1", "whatsapp")));
        throttled.Enqueue(new HttpResponseMessage((HttpStatusCode)429));
        outcome = await Channel(throttled, key, MessageChannel.WhatsApp).SendAsync(Message(booking, "t"), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.RetryAfter);                    // try again after the pause Wazzup asked for

        var rejected = new ScriptedHttp();
        rejected.Enqueue(Channels(ChannelJson("wa-1", "whatsapp")));
        rejected.Enqueue(Json(HttpStatusCode.BadRequest,
            """{"error":"INVALID_MESSAGE_DATA","description":"Message data is invalid"}"""));
        outcome = await Channel(rejected, key, MessageChannel.WhatsApp).SendAsync(Message(booking, "t"), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Skipped, outcome.Status);   // will not fix itself: no retries
        Assert.Contains("Message data is invalid", outcome.Error);

        var broken = new ScriptedHttp();
        broken.Enqueue(Channels(ChannelJson("wa-1", "whatsapp")));
        broken.Enqueue(Json(HttpStatusCode.InternalServerError, """{"error":"INTERNAL"}"""));
        outcome = await Channel(broken, key, MessageChannel.WhatsApp).SendAsync(Message(booking, "t"), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Failed, outcome.Status);     // temporary: retried later
    }

    [Fact]
    public async Task WithoutApiKey_NothingIsSentAnywhere()
    {
        var http = new ScriptedHttp();
        var channel = Channel(http, Config(), MessageChannel.WhatsApp);
        Assert.False(channel.IsConfigured);
        Assert.False(await channel.IsReadyAsync(CancellationToken.None));
        var outcome = await channel.SendAsync(Message(Booking(), "t"), CancellationToken.None);
        Assert.Equal(OutgoingMessageStatus.Failed, outcome.Status);
        Assert.Contains("WAZZUP_API_KEY", outcome.Error);
        Assert.Empty(http.Seen);                                // no request leaves the server without a key
    }

    [Fact]
    public async Task WazzupTroubles_AreVisibleOnTheState()
    {
        var http = new ScriptedHttp();
        http.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var state = await Channel(http, Config(("Wazzup:ApiKey", "bad")), MessageChannel.Telegram).StateAsync(CancellationToken.None);
        Assert.Equal("UNAUTHORIZED", state.Status);

        var unreachable = new ScriptedHttp();                   // nothing scripted: the call throws HttpRequestException
        state = await Channel(unreachable, Config(("Wazzup:ApiKey", "key")), MessageChannel.Telegram).StateAsync(CancellationToken.None);
        Assert.Equal("UNAVAILABLE", state.Status);

        var none = new ScriptedHttp();
        none.Enqueue(Channels(ChannelJson("wa-1", "whatsapp")));
        state = await Channel(none, Config(("Wazzup:ApiKey", "key")), MessageChannel.Telegram).StateAsync(CancellationToken.None);
        Assert.Equal("NO_CHANNEL", state.Status);
        Assert.Contains("Telegram", state.Detail);
    }
}
