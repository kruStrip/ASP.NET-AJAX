using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using MuzLoto.Web.Services.Messaging;

namespace MuzLoto.Web.Pages.Messengers;

/// <summary>Connects the Telegram account and the WhatsApp number the site writes from, and shows what was sent.</summary>
[Authorize(Roles = "Admin")]
public class IndexModel(AppDbContext db, TelegramChannel telegram, WhatsAppChannel whatsApp, RelayTunnel relay) : PageModel
{
    public TelegramChannel Telegram => telegram;
    public bool TelegramReady { get; private set; }
    public WhatsAppChannel.SessionState WhatsApp { get; private set; } = new("UNKNOWN", null);
    public bool WhatsAppConfigured => whatsApp.IsConfigured;
    public List<OutgoingMessage> Recent { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        TelegramReady = await telegram.IsReadyAsync(ct);
        WhatsApp = await whatsApp.StateAsync(ct);
        Recent = await db.OutgoingMessages.AsNoTracking().Include(x => x.Booking).ThenInclude(x => x.GameSession).ThenInclude(x => x.Game)
            .OrderByDescending(x => x.CreatedAt).Take(60).ToListAsync(ct);
    }

    public Task<IActionResult> OnPostTelegramQrAsync(string? password, CancellationToken ct) => Run(async () =>
    {
        await telegram.StartQrLoginAsync(password, ct);
        await Task.Delay(1500, ct);   // the first code arrives within a second
        return "Отсканируйте QR-код в Telegram: Настройки → Устройства → Подключить устройство. Код действует 5 минут.";
    });

    public IActionResult OnGetTelegramQr()
    {
        if (telegram.QrPng() is not { } png) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(png, "image/png");
    }

    public Task<IActionResult> OnPostTelegramLogoutAsync(CancellationToken ct) => Run(async () =>
    {
        await telegram.LogoutAsync(ct);
        return "Telegram отключён.";
    });

    public Task<IActionResult> OnPostWhatsAppStartAsync(CancellationToken ct) => Run(async () =>
    {
        await whatsApp.StartAsync(ct);
        return "Сессия WhatsApp запущена. Отсканируйте QR-код телефоном с этим номером: WhatsApp → Связанные устройства → Привязка устройства.";
    });

    public bool RelayConfigured => relay.IsConfigured;

    public Task<IActionResult> OnPostRelayTestAsync(CancellationToken ct) => Run(() => relay.SelfTestAsync(ct));

    public Task<IActionResult> OnPostWhatsAppLogoutAsync(CancellationToken ct) => Run(async () =>
    {
        await whatsApp.LogoutAsync(ct);
        return "WhatsApp отключён.";
    });

    /// <summary>The QR code to scan, served through the site: the gateway itself is not reachable from outside.</summary>
    public async Task<IActionResult> OnGetWhatsAppQrAsync(CancellationToken ct)
    {
        var png = await whatsApp.QrPngAsync(ct);
        if (png is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(png, "image/png");
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid id, CancellationToken ct)
    {
        var message = await db.OutgoingMessages.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (message is null) return NotFound();
        message.Status = OutgoingMessageStatus.Pending;
        message.Attempts = 0;
        message.NextAttemptAt = DateTimeOffset.UtcNow;
        message.LastError = null;
        await db.SaveChangesAsync(ct);
        TempData["Notice"] = "Сообщение снова в очереди.";
        return RedirectToPage();
    }

    private async Task<IActionResult> Run(Func<Task<string>> action)
    {
        try { TempData["Notice"] = await action(); }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        catch (HttpRequestException) { TempData["Error"] = "WhatsApp-шлюз недоступен. Проверьте, что контейнер waha запущен."; }
        return RedirectToPage();
    }

    public static string StatusText(OutgoingMessageStatus s) => s switch
    {
        OutgoingMessageStatus.Sent => "Отправлено", OutgoingMessageStatus.Skipped => "Не отправлено",
        OutgoingMessageStatus.Failed => "Ошибка", _ => "В очереди"
    };

    public static string WhatsAppText(string status) => status switch
    {
        "WORKING" => "подключён", "SCAN_QR_CODE" => "ждёт сканирования QR-кода", "STARTING" => "запускается…",
        "STOPPED" or "NONE" => "не запущен", "FAILED" => "ошибка — запустите заново",
        "NOT_CONFIGURED" => "не настроен на сервере",
        "UNAVAILABLE" => "шлюз не отвечает (контейнер waha не запущен или ещё запускается)",
        "UNAUTHORIZED" => "шлюз отклонил ключ: WAHA_API_KEY у сайта и у шлюза не совпадают",
        _ when status.StartsWith("HTTP_") => "шлюз ответил ошибкой " + status[5..], _ => status
    };
}
