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
/// </summary>
public sealed class TelegramBotChannel(IHttpClientFactory httpFactory, IConfiguration config, IReceiptFiles files)
    : IMessengerChannel
{
    public MessageChannel Channel => MessageChannel.TelegramBot;

    private string? Token => config["Telegram:BotToken"]?.Trim();
    private string? ChatId => config["Telegram:ManagerChatId"]?.Trim();
    private string? TopicId => config["Telegram:ManagerTopicId"]?.Trim();
    private string? ContactTopicId => config["Telegram:ContactTopicId"]?.Trim();
    private HttpClient Http => httpFactory.CreateClient("TelegramBot");

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
            using var response = files.OpenReceipt(message.Booking) is { } receipt
                ? await SendFileAsync(token, chatId, topic, receipt, message.Text, ct)
                : await SendTextAsync(token, chatId, topic, message.Text, ct);
            if (response.StatusCode == HttpStatusCode.OK) return SendOutcome.Sent;
            return await ExplainAsync(response, ct);
        }
        catch (HttpRequestException) { return SendOutcome.Failed("Telegram недоступен, повторим позже."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return SendOutcome.Failed("Telegram не ответил за 30 секунд."); }
    }

    private Task<HttpResponseMessage> SendTextAsync(string token, string chatId, string? topic, string text, CancellationToken ct)
    {
        object body = WithTopic(new Dictionary<string, object?> { ["chat_id"] = chatId, ["text"] = text }, topic);
        return Http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", body, ct);
    }

    private async Task<HttpResponseMessage> SendFileAsync(string token, string chatId, string? topic,
        (string Path, string ContentType) file, string caption, CancellationToken ct)
    {
        var isPhoto = file.ContentType != "application/pdf";
        // Telegram rejects captions longer than 1024 characters; the text is what a manager forwards, keep the start.
        if (caption.Length > 1024) caption = caption[..1023] + "…";
        using var form = new MultipartFormDataContent();
        AddField(form, "chat_id", chatId);
        if (topic is { Length: > 0 }) AddField(form, "message_thread_id", topic);
        AddField(form, "caption", caption);
        var content = new StreamContent(File.OpenRead(file.Path));
        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        form.Add(content, isPhoto ? "photo" : "document", Path.GetFileName(file.Path));
        var method = isPhoto ? "sendPhoto" : "sendDocument";
        // Await here: the form and the file stream must stay alive until the upload has been sent.
        return await Http.PostAsync($"https://api.telegram.org/bot{token}/{method}", form, ct);
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
    private static async Task<SendOutcome> ExplainAsync(HttpResponseMessage response, CancellationToken ct)
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
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => SendOutcome.Failed("Telegram отклонил токен бота — проверьте TELEGRAM_BOT_TOKEN."),
            HttpStatusCode.Forbidden => SendOutcome.Failed("Бот не может писать в группу — добавьте его в группу и проверьте тему."),
            HttpStatusCode.BadRequest => SendOutcome.Skipped("Telegram не принял сообщение: " + why + "."),
            HttpStatusCode.TooManyRequests => SendOutcome.Failed("Telegram просит сбавить темп, повторим позже.",
                TimeSpan.FromSeconds(Math.Max(60, retryAfter ?? 60))),
            _ => SendOutcome.Failed("Telegram ответил ошибкой: " + why + ".", TimeSpan.FromMinutes(2))
        };
    }
}
