using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// Sends the managers' notifications to Telegram group topics through a bot: «Подтвердить игру» for a new receipt
/// and «Связаться с клиентом» when the customer's confirmation could not be delivered. The bot is added to the group
/// by the company; the site stores only its token and the chat/topic ids (Telegram:BotToken, Telegram:ManagerChatId,
/// Telegram:ManagerTopicId, Telegram:ContactTopicId — see docs/DEPLOY.md).
/// The receipt file is uploaded from the local storage, so it is never exposed by a public link.
/// When the server cannot reach api.telegram.org (Telegram is blocked for the hosting), the calls go through a small
/// relay that forwards only the bot methods we use: Telegram:ApiBaseUrl points at it and Telegram:RelaySecret is the
/// shared header it requires (see deploy/cloudflare-worker — the decision of 2026-11-03).
/// Through a relay the file itself is not uploaded: the provider's filtering drops request bodies over ~10 KB to the
/// relay's hostname (measured 2026-11-03), so the site sends a small JSON with the receipt's signed link and the relay
/// downloads the file from our site and hands it to Telegram. Direct calls (no relay) still upload the file.
/// </summary>
public sealed class TelegramBotChannel(
    IHttpClientFactory httpFactory, IConfiguration config, IReceiptFiles files, ReceiptLinks links)
    : IMessengerChannel
{
    public MessageChannel Channel => MessageChannel.TelegramBot;

    private string? Token => config["Telegram:BotToken"]?.Trim();
    private string? ChatId => config["Telegram:ManagerChatId"]?.Trim();
    private string? TopicId => config["Telegram:ManagerTopicId"]?.Trim();
    private string? ContactTopicId => config["Telegram:ContactTopicId"]?.Trim();
    private string? RelaySecret => config["Telegram:RelaySecret"]?.Trim();

    /// <summary>The calls go through a relay (Telegram is unreachable from this server) rather than to api.telegram.org.</summary>
    private bool ThroughRelay => config["Telegram:ApiBaseUrl"]?.Trim() is { Length: > 0 };

    /// <summary>api.telegram.org itself, or the relay through which this server can reach it.</summary>
    private string ApiBaseUrl =>
        (config["Telegram:ApiBaseUrl"]?.Trim() is { Length: > 0 } url ? url : "https://api.telegram.org").TrimEnd('/');

    private HttpClient Http
    {
        get
        {
            var http = httpFactory.CreateClient("TelegramBot");
            // The relay refuses calls without the shared secret; Telegram itself ignores an extra header.
            if (RelaySecret is { Length: > 0 } secret) http.DefaultRequestHeaders.TryAddWithoutValidation("X-Relay-Secret", secret);
            return http;
        }
    }

    /// <summary>«Подтвердить игру» for receipts, «Связаться с клиентом» when the customer got nothing.</summary>
    private string? TopicFor(OutgoingMessage message) =>
        message.Kind == OutgoingMessageKind.ContactRequest ? ContactTopicId : TopicId;

    /// <summary>No token or chat id means the group was not set up yet; messages simply wait in the queue.</summary>
    public Task<bool> IsReadyAsync(CancellationToken ct) =>
        Task.FromResult(Token is { Length: > 0 } && ChatId is { Length: > 0 });

    public async Task<SendOutcome> SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        if (Token is not { Length: > 0 } token) return SendOutcome.Failed("Не задан TELEGRAM_BOT_TOKEN — уведомления менеджеру не отправляются.");
        if (ChatId is not { Length: > 0 } chatId) return SendOutcome.Failed("Не задан TELEGRAM_MANAGER_CHAT_ID — уведомления менеджеру не отправляются.");
        var topic = TopicFor(message);
        if (message.Kind == OutgoingMessageKind.ContactRequest && topic is not { Length: > 0 })
            return SendOutcome.Failed("Не задан TELEGRAM_CONTACT_TOPIC_ID — уведомление «Связаться с клиентом» не отправляется.");
        try
        {
            using var response = await SendCoreAsync(token, chatId, topic, message, ct);
            if (response.StatusCode == HttpStatusCode.OK) return SendOutcome.Sent;
            return await ExplainAsync(response, ct);
        }
        catch (HttpRequestException) { return SendOutcome.Failed("Telegram недоступен, повторим позже."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return SendOutcome.Failed("Telegram не ответил за 30 секунд."); }
    }

    /// <summary>Text when there is nothing to attach, otherwise the receipt — uploaded or, through a relay, sent by link.</summary>
    private Task<HttpResponseMessage> SendCoreAsync(string token, string chatId, string? topic,
        OutgoingMessage message, CancellationToken ct)
    {
        if (message.Booking.ReceiptKey is not { Length: > 0 } key) return SendTextAsync(token, chatId, topic, message, ct);
        if (ThroughRelay) return SendFileByLinkAsync(token, chatId, topic, message, key, ct);
        return files.OpenReceipt(message.Booking) is { } file
            ? SendFileAsync(token, chatId, topic, file, message, ct)
            : SendTextAsync(token, chatId, topic, message, ct);
    }

    /// <summary>
    /// The text to show. «Связаться с клиентом» is sent as HTML with the customer's block in &lt;pre&gt;, so Telegram
    /// draws a «Copy» button next to it (decision of 2026-11-03); everything else stays plain text.
    /// </summary>
    private static bool AsHtml(OutgoingMessage message) => message.Kind == OutgoingMessageKind.ContactRequest;

    private static string TelegramText(OutgoingMessage message) =>
        AsHtml(message) ? ContactRequestMessage.ToTelegramHtml(message.Text) : message.Text;

    private Task<HttpResponseMessage> SendTextAsync(string token, string chatId, string? topic,
        OutgoingMessage message, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["chat_id"] = chatId, ["text"] = TelegramText(message) };
        if (AsHtml(message)) body["parse_mode"] = "HTML";
        return Http.PostAsJsonAsync($"{ApiBaseUrl}/bot{token}/sendMessage", WithTopic(body, topic), ct);
    }

    /// <summary>
    /// The relay downloads the receipt from the signed link and uploads it to Telegram. The body stays a few hundred
    /// bytes on purpose: the request the site sends must fit under the size the provider lets through.
    /// </summary>
    private Task<HttpResponseMessage> SendFileByLinkAsync(string token, string chatId, string? topic,
        OutgoingMessage message, string receiptKey, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["token"] = token,
            ["chatId"] = chatId,
            ["caption"] = Caption(message),
            ["fileName"] = Path.GetFileName(receiptKey),
            ["fileUrl"] = links.Create(message.Booking.Id, receiptKey)
        };
        if (AsHtml(message)) body["parseMode"] = "HTML";
        if (topic is { Length: > 0 }) body["topicId"] = topic;
        return Http.PostAsJsonAsync($"{ApiBaseUrl}/send-file", body, ct);
    }

    /// <summary>Telegram rejects captions longer than 1024 characters; the text is what a manager forwards, keep the start.</summary>
    private static string Caption(OutgoingMessage message)
    {
        var caption = TelegramText(message);
        return caption.Length > 1024 ? caption[..1023] + "…" : caption;
    }

    private async Task<HttpResponseMessage> SendFileAsync(string token, string chatId, string? topic,
        (string Path, string ContentType) file, OutgoingMessage message, CancellationToken ct)
    {
        var isPhoto = file.ContentType != "application/pdf";
        using var form = new MultipartFormDataContent();
        AddField(form, "chat_id", chatId);
        if (topic is { Length: > 0 }) AddField(form, "message_thread_id", topic);
        AddField(form, "caption", Caption(message));
        if (AsHtml(message)) AddField(form, "parse_mode", "HTML");
        var content = new StreamContent(File.OpenRead(file.Path));
        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        form.Add(content, isPhoto ? "photo" : "document", Path.GetFileName(file.Path));
        var method = isPhoto ? "sendPhoto" : "sendDocument";
        // Await here: the form and the file stream must stay alive until the upload has been sent.
        return await Http.PostAsync($"{ApiBaseUrl}/bot{token}/{method}", form, ct);
    }

    private static object WithTopic(Dictionary<string, object?> body, string? topic)
    {
        // The Bot API wants a number here; a numeric string works in form fields but not always in JSON.
        if (topic is { Length: > 0 }) body["message_thread_id"] = long.TryParse(topic, out var id) ? id : topic;
        return body;
    }

    private static void AddField(MultipartFormDataContent form, string name, string value) =>
        form.Add(new StringContent(value), name);

    /// <summary>Turns Telegram's «ok»: false answer into a reason staff can act on.</summary>
    private async Task<SendOutcome> ExplainAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? description = null;
        int? retryAfter = null;
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (json.RootElement.TryGetProperty("description", out var d)) description = d.GetString();
            if (json.RootElement.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var r))
                retryAfter = r.GetInt32();
        }
        catch (JsonException) { }
        var why = description ?? ("HTTP " + (int)response.StatusCode);
        // A relay in front of Telegram answers with its own 401/403 and no Bot API JSON: say which secret to check.
        var relayRefused = description is null && RelaySecret is { Length: > 0 };
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized when relayRefused =>
                SendOutcome.Failed("Релей Telegram отклонил запрос — проверьте TELEGRAM_RELAY_SECRET."),
            HttpStatusCode.Forbidden when relayRefused =>
                SendOutcome.Failed("Релей Telegram отклонил запрос — проверьте TELEGRAM_RELAY_SECRET."),
            // An older relay without /send-file answers a plain 404: the site must say what to update.
            HttpStatusCode.NotFound when relayRefused =>
                SendOutcome.Failed("Релей Telegram не умеет отправлять чек по ссылке — обновите deploy/cloudflare-worker/worker.js."),
            HttpStatusCode.BadGateway when ThroughRelay && description is not null =>
                SendOutcome.Failed(why, TimeSpan.FromMinutes(2)),
            HttpStatusCode.Unauthorized => SendOutcome.Failed("Telegram отклонил токен бота — проверьте TELEGRAM_BOT_TOKEN."),
            HttpStatusCode.Forbidden => SendOutcome.Failed("Бот не может писать в группу — добавьте его в группу и проверьте тему."),
            HttpStatusCode.BadRequest => SendOutcome.Skipped("Telegram не принял сообщение: " + why + "."),
            HttpStatusCode.TooManyRequests => SendOutcome.Failed("Telegram просит сбавить темп, повторим позже.",
                TimeSpan.FromSeconds(Math.Max(60, retryAfter ?? 60))),
            _ => SendOutcome.Failed("Telegram ответил ошибкой: " + why + ".", TimeSpan.FromMinutes(2))
        };
    }
}
