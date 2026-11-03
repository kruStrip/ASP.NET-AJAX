using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using MuzLoto.Web.Services.Messaging;

namespace MuzLoto.Web.Pages.Messengers;

/// <summary>
/// Shows the state of the Wazzup channels the site sends «ваша игра подтверждена» from (the accounts themselves
/// are connected in the Wazzup web interface, not here), and the journal of messages with a retry button.
/// </summary>
[Authorize(Roles = "Admin")]
public class IndexModel(AppDbContext db, IEnumerable<IMessengerChannel> channels) : PageModel
{
    private WazzupChannel Telegram { get; } = channels.OfType<WazzupChannel>().Single(c => c.Channel == MessageChannel.Telegram);
    private WazzupChannel WhatsApp { get; } = channels.OfType<WazzupChannel>().Single(c => c.Channel == MessageChannel.WhatsApp);

    public WazzupState TelegramState { get; private set; } = WazzupState.Of("UNKNOWN");
    public WazzupState WhatsAppState { get; private set; } = WazzupState.Of("UNKNOWN");
    public bool Configured => Telegram.IsConfigured;
    public List<OutgoingMessage> Recent { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        TelegramState = await Telegram.StateAsync(ct);
        WhatsAppState = await WhatsApp.StateAsync(ct);
        Recent = await db.OutgoingMessages.AsNoTracking().Include(x => x.Booking).ThenInclude(x => x.GameSession).ThenInclude(x => x.Game)
            .OrderByDescending(x => x.CreatedAt).Take(60).ToListAsync(ct);
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

    public static string StatusText(OutgoingMessageStatus s) => s switch
    {
        OutgoingMessageStatus.Sent => "Отправлено", OutgoingMessageStatus.Skipped => "Не отправлено",
        OutgoingMessageStatus.Failed => "Ошибка", _ => "В очереди"
    };

    /// <summary>Wazzup channel states, in words for the admin.</summary>
    public static string StateText(string state) => state switch
    {
        "NOT_CONFIGURED" => "не настроен",
        "UNAUTHORIZED" => "ключ API отклонён",
        "UNAVAILABLE" => "сервис Wazzup недоступен",
        "NO_CHANNEL" => "канал не подключён в Wazzup",
        "active" => "работает",
        "init" => "запускается",
        "qridle" => "нужно отсканировать QR-код в Wazzup",
        "phoneUnavailable" => "нет связи с телефоном, на котором подключён мессенджер",
        "openelsewhere" => "канал авторизован в другом аккаунте Wazzup",
        "notEnoughMoney" => "канал не оплачен в Wazzup",
        "foreignphone" => "QR-код отсканирован не тем номером",
        "unauthorized" => "не авторизован",
        "waitForPassword" => "нужен пароль двухэтапной проверки",
        "blocked" => "канал заблокирован",
        "onModeration" => "канал на модерации",
        "rejected" => "канал отклонён",
        "disabled" => "канал выключен",
        _ => state
    };

    public static string TransportText(string transport) => transport switch
    {
        "whatsapp" => "WhatsApp", "wapi" => "WhatsApp Business API", "tgapi" => "Telegram", "telegram" => "Telegram Bot",
        "max" => "MAX", "maxbot" => "MAX Bot", "vk" => "ВКонтакте", "instagram" => "Instagram*", "viber" => "Viber",
        "avito" => "Авито", _ => transport
    };
}
