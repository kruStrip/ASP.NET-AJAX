using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// Sends from a separate WhatsApp number through WAHA (a self-hosted WhatsApp Web gateway in its own container,
/// reachable only inside the server's docker network). The admin links the number once by scanning a QR code on
/// «Мессенджеры». Configuration: Waha:Url and Waha:ApiKey.
/// </summary>
public sealed class WhatsAppChannel(HttpClient http, IConfiguration config) : IMessengerChannel
{
    private const string Session = "default";
    public MessageChannel Channel => MessageChannel.WhatsApp;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Waha:Url"]) && !string.IsNullOrWhiteSpace(config["Waha:ApiKey"]);

    public sealed record SessionState(string Status, string? Account);

    /// <summary>WAHA session status: STOPPED, STARTING, SCAN_QR_CODE, WORKING, FAILED (or NONE when not created).</summary>
    public async Task<SessionState> StateAsync(CancellationToken ct)
    {
        if (!IsConfigured) return new("NOT_CONFIGURED", null);
        try
        {
            using var response = await http.GetAsync($"api/sessions/{Session}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new("NONE", null);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var me = json.TryGetProperty("me", out var m) && m.ValueKind == JsonValueKind.Object
                ? (m.TryGetProperty("pushName", out var n) ? n.GetString() : null) ?? (m.TryGetProperty("id", out var id) ? id.GetString() : null)
                : null;
            return new(json.GetProperty("status").GetString() ?? "UNKNOWN", me);
        }
        catch (HttpRequestException) { return new("UNAVAILABLE", null); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new("UNAVAILABLE", null); }
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct) => (await StateAsync(ct)).Status == "WORKING";

    /// <summary>Creates and starts the session (or starts the existing one); WAHA then shows a QR code to scan.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        using var create = await http.PostAsJsonAsync("api/sessions", new { name = Session }, ct);
        if (create.IsSuccessStatusCode) return;
        using var start = await http.PostAsync($"api/sessions/{Session}/start", null, ct);
        if (!start.IsSuccessStatusCode && start.StatusCode != HttpStatusCode.UnprocessableEntity)
            throw new BusinessException("WhatsApp-шлюз не запустил сессию: " + (int)start.StatusCode);
    }

    public async Task<byte[]?> QrPngAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/{Session}/auth/qr");
        request.Headers.Accept.ParseAdd("image/png");
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        using var _ = await http.PostAsync($"api/sessions/{Session}/logout", null, ct);
    }

    public async Task<SendOutcome> SendAsync(Booking booking, string text, CancellationToken ct)
    {
        var phone = new string(booking.Phone.Where(char.IsDigit).ToArray());
        try
        {
            using var check = await http.GetAsync($"api/contacts/check-exists?phone={phone}&session={Session}", ct);
            if (!check.IsSuccessStatusCode) return SendOutcome.Failed($"WhatsApp-шлюз ответил {(int)check.StatusCode} при проверке номера.");
            var exists = await check.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!exists.TryGetProperty("numberExists", out var found) || !found.GetBoolean())
                return SendOutcome.Skipped("У номера нет WhatsApp.");
            var chatId = exists.TryGetProperty("chatId", out var c) && c.GetString() is { Length: > 0 } id ? id : phone + "@c.us";
            using var send = await http.PostAsJsonAsync("api/sendText", new { session = Session, chatId, text }, ct);
            return send.IsSuccessStatusCode ? SendOutcome.Sent : SendOutcome.Failed($"WhatsApp-шлюз ответил {(int)send.StatusCode}.");
        }
        catch (HttpRequestException) { return SendOutcome.Failed("WhatsApp-шлюз недоступен."); }
    }
}
