using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>A channel of the Wazzup account as its API reports it (GET /v3/channels).</summary>
public sealed record WazzupChannelInfo(string ChannelId, string Transport, string? PlainId, string State);

/// <summary>Everything the channel knows about its messenger: a status, the channel list and the one we send from.</summary>
public sealed record WazzupState(string Status, string? Detail, IReadOnlyList<WazzupChannelInfo> Channels, WazzupChannelInfo? Resolved = null)
{
    public static WazzupState Of(string status, string? detail = null) => new(status, detail, []);
}

/// <summary>
/// Sends customer messages («мы подтвердили вашу бронь» and the customer's own receipt copy) through the Wazzup
/// cloud service (api.wazzup24.com). The company connects its
/// WhatsApp number and Telegram account in the Wazzup web interface and scans the QR codes there; the site only
/// calls the Wazzup API with an API key, so no messenger sessions or gateway containers live on our server.
/// Configuration: Wazzup:ApiKey (required) and optional Wazzup:WhatsAppChannelId / Wazzup:TelegramChannelId —
/// without an id the only channel of the matching type is picked automatically (a plain WhatsApp number before
/// WABA, a Telegram account before a bot); several channels of one type block sending until one is pinned.
/// </summary>
public sealed class WazzupChannel(IHttpClientFactory httpFactory, IConfiguration config, MessageChannel channel) : IMessengerChannel
{
    /// <summary>The named client registered in Program.cs: base address, Bearer key and timeout live there.</summary>
    private HttpClient Http => httpFactory.CreateClient("Wazzup");

    /// <summary>Channel states are re-read at most this often (the dispatcher asks before every batch).</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    public MessageChannel Channel => channel;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Wazzup:ApiKey"]);
    private string Name => channel is MessageChannel.WhatsApp ? "WhatsApp" : "Telegram";
    private string EnvChannelId => channel is MessageChannel.WhatsApp ? "WAZZUP_WHATSAPP_CHANNEL_ID" : "WAZZUP_TELEGRAM_CHANNEL_ID";
    private string[] Transports => channel switch
    {
        // a plain WhatsApp number before WABA, a personal Telegram account before a bot
        MessageChannel.WhatsApp => ["whatsapp", "wapi"],
        _ => ["tgapi", "telegram"]
    };

    private WazzupState? cached;
    private DateTimeOffset cachedAt;

    /// <summary>Everything Wazzup knows about this messenger right now, cached for the dispatcher's batches.</summary>
    public async Task<WazzupState> StateAsync(CancellationToken ct)
    {
        if (!IsConfigured)
            return WazzupState.Of("NOT_CONFIGURED", "На сервере в .env не задан WAZZUP_API_KEY.");
        if (cached is { } state && DateTimeOffset.UtcNow - cachedAt < CacheLifetime) return state;
        try
        {
            using var response = await Http.GetAsync("v3/channels", ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return Cache(WazzupState.Of("UNAUTHORIZED", "Wazzup отклонил ключ API — проверьте WAZZUP_API_KEY."));
            if (!response.IsSuccessStatusCode)
                return Cache(WazzupState.Of("UNAVAILABLE", "Wazzup ответил " + (int)response.StatusCode + "."));
            var channels = await response.Content.ReadFromJsonAsync<List<WazzupChannelInfo>>(ct) ?? [];
            return Cache(Describe(MatchChannel(channels), channels));
        }
        catch (HttpRequestException) { return Cache(WazzupState.Of("UNAVAILABLE", "Сервис Wazzup недоступен.")); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Cache(WazzupState.Of("UNAVAILABLE", "Сервис Wazzup не ответил за 30 секунд."));
        }
    }

    private WazzupState NoChannel(string? configuredId) => configuredId is { Length: > 0 }
        ? WazzupState.Of("NO_CHANNEL", $"В Wazzup нет канала «{Name}» с ID «{configuredId}» из {EnvChannelId} — проверьте значение или очистите его.")
        : WazzupState.Of("NO_CHANNEL", $"В аккаунте Wazzup не подключён канал «{Name}».");

    private WazzupState Cache(WazzupState state)
    {
        cached = state;
        cachedAt = DateTimeOffset.UtcNow;
        return state;
    }

    /// <summary>Turns the channel list into a state: which channel we send from and whether it is active.</summary>
    private WazzupState Describe((WazzupChannelInfo Channel, bool Several)? match, IReadOnlyList<WazzupChannelInfo> all)
    {
        if (match is not { } found)
            return NoChannel(config[channel is MessageChannel.WhatsApp ? "Wazzup:WhatsAppChannelId" : "Wazzup:TelegramChannelId"]?.Trim());
        // Several channels of the same messenger: guessing means writing to customers from the wrong account
        // (that happened on 2026-11-03 — the personal account answered instead of the company's). Nothing is sent
        // until a manager pins the intended channel with its id.
        if (found.Several)
        {
            var candidates = all.Where(c => Transports.Contains(c.Transport))
                .Select(c => c.PlainId is { Length: > 0 } plain ? plain : c.ChannelId);
            return new("AMBIGUOUS",
                $"В Wazzup несколько каналов «{Name}» ({string.Join(", ", candidates)}) — с какого писать, не угадать. " +
                $"Укажите нужный в {EnvChannelId} на сервере: до этого сообщения ждут.", all);
        }
        var detail = found.Channel.PlainId is { Length: > 0 } plainId ? plainId : null;
        return new(found.Channel.State, detail, all, found.Channel);
    }

    /// <summary>The channel to send from: the configured id, else the best channel of the matching type.</summary>
    private (WazzupChannelInfo Channel, bool Several)? MatchChannel(IReadOnlyList<WazzupChannelInfo> channels)
    {
        var configured = config[channel is MessageChannel.WhatsApp ? "Wazzup:WhatsAppChannelId" : "Wazzup:TelegramChannelId"]?.Trim();
        if (configured is { Length: > 0 })
        {
            var byId = channels.FirstOrDefault(c => string.Equals(c.ChannelId, configured, StringComparison.OrdinalIgnoreCase));
            return byId is null ? null : (byId, false);
        }
        foreach (var transport in Transports)   // a plain WhatsApp number before WABA, a Telegram account before a bot
        {
            var candidates = channels.Where(c => c.Transport == transport).ToList();
            if (candidates.Count == 0) continue;
            var working = candidates.Where(c => c.State == "active").ToList();
            var pick = working.Count > 0 ? working : candidates;
            return (pick[0], candidates.Count > 1);
        }
        return null;
    }

    /// <summary>Signed in to Wazzup and the channel is active: messages can go out now.</summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct) => (await StateAsync(ct)).Status == "active";

    public async Task<SendOutcome> SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        var state = await StateAsync(ct);
        switch (state.Status)
        {
            case "NOT_CONFIGURED": return SendOutcome.Failed("Wazzup не настроен: на сервере нет WAZZUP_API_KEY.");
            case "UNAUTHORIZED": return SendOutcome.Failed("Wazzup отклонил ключ API — проверьте WAZZUP_API_KEY.");
            case "UNAVAILABLE": return SendOutcome.Failed("Сервис Wazzup недоступен, повторим позже.");
            case "NO_CHANNEL": return SendOutcome.Failed("В Wazzup не найден канал «" + Name + "» — подключите его и повторите.");
            case "AMBIGUOUS": return SendOutcome.Failed(state.Detail ?? $"В Wazzup несколько каналов «{Name}» — закрепите нужный в {EnvChannelId}.");
            case "active": break;
            default: return SendOutcome.Failed($"Канал «{Name}» в Wazzup не активен ({state.Status}) — сообщение будет ждать.");
        }
        var channelId = state.Resolved!.ChannelId;
        object body = BuildBody(message.Booking, channelId, message);
        try
        {
            using var send = await Http.PostAsJsonAsync("v3/message", body, ct);
            if (send.StatusCode == HttpStatusCode.Created) return SendOutcome.Sent;
            return await ExplainAsync(send, ct);
        }
        catch (HttpRequestException) { return SendOutcome.Failed("Сервис Wazzup недоступен."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return SendOutcome.Failed("Сервис Wazzup не ответил за 30 секунд."); }
    }

    private object BuildBody(Booking booking, string channelId, OutgoingMessage message)
    {
        var body = new Dictionary<string, object?> { ["channelId"] = channelId };
        if (channel is MessageChannel.WhatsApp)
        {
            body["chatType"] = "whatsapp";
            body["chatId"] = Digits(booking.Phone);
        }
        else
        {
            body["chatType"] = "telegram";
            // Telegram: the nickname from the booking form first; without it — the phone number.
            var nick = booking.Telegram?.Trim().TrimStart('@');
            if (nick is { Length: > 0 }) body["username"] = nick;
            else body["phone"] = Digits(booking.Phone);
        }
        // Wazzup sends either text or a file, never both: an image message has contentUri instead of text.
        if (message.ContentUri is { Length: > 0 } uri) body["contentUri"] = uri;
        else body["text"] = message.Text;
        return body;
    }

    private static async Task<SendOutcome> ExplainAsync(HttpResponseMessage send, CancellationToken ct)
    {
        string? code = null, description = null;
        try
        {
            var error = await send.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (error.TryGetProperty("error", out var e)) code = e.GetString();
            if (error.TryGetProperty("description", out var d)) description = d.GetString();
        }
        catch (JsonException) { }
        var why = description ?? code;
        switch (send.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return SendOutcome.Failed("Wazzup отклонил ключ API — проверьте WAZZUP_API_KEY.");
            case HttpStatusCode.TooManyRequests:
                return SendOutcome.Failed("Wazzup просит сбавить темп, повторим через минуту.", TimeSpan.FromMinutes(1));
            case HttpStatusCode.NotFound:
                return SendOutcome.Failed("Канал не найден в Wazzup — проверьте WAZZUP_*_CHANNEL_ID.");
            case HttpStatusCode.BadRequest when code == "WRONG_TRANSPORT":
                return SendOutcome.Skipped("Канал Wazzup не подходит для этого мессенджера — проверьте WAZZUP_*_CHANNEL_ID.");
            case HttpStatusCode.BadRequest:
                // A recipient Wazzup cannot deliver to (no WhatsApp on the number, no such Telegram user) will not fix itself.
                return SendOutcome.Skipped("Wazzup не принял сообщение: " + (why ?? "ошибка запроса") + ".");
            default:
                return SendOutcome.Failed("Wazzup ответил " + (int)send.StatusCode + (why is null ? "." : ": " + why + "."), TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>Wazzup wants digits only, in the international format: +7 914 000-00-00 → 79140000000.</summary>
    private static string Digits(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length == 11 && digits[0] == '8' ? '7' + digits[1..] : digits;
    }
}
