using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages;

/// <summary>
/// The customer booking form (replaces FormDesigner): date → game card → contacts → «Пожалуйста, перепроверьте»
/// → «К оплате». The booking is created as Pending and takes no seats until staff confirm the payment.
/// </summary>
/// <remarks>
/// The anti-forgery token is checked by hand here: a token goes stale when the customer signs in or out in another
/// tab or comes back with «Назад» after booking (booking signs the customer in). Instead of a bare 400 the form
/// comes back with what was typed and a fresh token. CSRF protection stays: nothing is saved without a valid token.
/// </remarks>
[IgnoreAntiforgeryToken]
public class HomeModel(PublicSchedule schedule, BookingWorkflow workflow, PaymentLinks paymentLinks, IConfiguration config,
    ClientAccounts accounts, SignInManager<AppUser> signIn, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) : PageModel
{
    private const string StaleForm = "Страница устарела — например, вы вошли или вышли из аккаунта. Проверьте данные и нажмите ещё раз.";

    private async Task<bool> TokenIsValidAsync() => await antiforgery.IsRequestValidAsync(HttpContext);

    public enum Steps { Form, Review }

    public List<GameOffer> Offers { get; private set; } = new();
    public GameOffer? Selected { get; private set; }
    public Steps Step { get; private set; } = Steps.Form;
    public string? PrivacyPolicyUrl => config["Legal:PrivacyPolicyUrl"];
    public decimal Total => Selected is null || Input.Quantity is not { } q ? 0 : Selected.Price * q;

    [BindProperty] public BookingInput Input { get; set; } = new();
    public sealed class BookingInput
    {
        public Guid SessionId { get; set; }
        /// <summary>Generated when the form is shown: a double click or a resent POST cannot create a second booking.</summary>
        public Guid SubmissionKey { get; set; }
        [Required(ErrorMessage = "Выберите количество билетов.")]
        public int? Quantity { get; set; }
        [Required(ErrorMessage = "Введите ваше имя."), StringLength(150, ErrorMessage = "Имя: не длиннее 150 символов.")]
        public string? Name { get; set; }
        [Required(ErrorMessage = "Введите ваш телефон.")]
        public string? Phone { get; set; }
        public string? Telegram { get; set; }
        [Required(ErrorMessage = "Введите название команды."), StringLength(150, ErrorMessage = "Название команды: не длиннее 150 символов.")]
        public string? Team { get; set; }
        [Range(typeof(bool), "true", "true", ErrorMessage = "Нужно согласие на обработку персональных данных.")]
        public bool AcceptPrivacy { get; set; }
        /// <summary>Hidden from people; bots that fill every field are turned away.</summary>
        public string? Website { get; set; }
    }

    public async Task OnGetAsync(Guid? game, CancellationToken ct)
    {
        await LoadAsync(game, ct);
        if (Selected is not null) Input.SessionId = Selected.Id;
        Input.SubmissionKey = Guid.NewGuid();
    }

    public async Task<IActionResult> OnPostReviewAsync(CancellationToken ct)
    {
        if (!await TokenIsValidAsync()) return await StaleAsync(ct);
        if (!await ValidateAsync(ct)) return Page();
        Step = Steps.Review;
        return Page();
    }

    public async Task<IActionResult> OnPostEditAsync(CancellationToken ct)
    {
        // «Назад» only shows the form again with the typed values: nothing is saved, no token needed.
        ModelState.Clear();
        await LoadAsync(Input.SessionId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSubmitAsync(CancellationToken ct)
    {
        if (!await TokenIsValidAsync()) return await StaleAsync(ct);
        if (!await ValidateAsync(ct)) return Page();
        if (!string.IsNullOrEmpty(Input.Website)) return RedirectToPage(); // bot: pretend nothing happened
        try
        {
            // The booking goes to the customer's personal account: the signed-in one, or the account of this phone.
            // A new phone gets an account with a generated password and is signed in at once. An existing phone is
            // never signed in from the form (anyone could type someone else's number): that customer pays and
            // uploads the receipt on the payment screen and sees the booking after signing in.
            var ownerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            ClientAccounts.Result? account = null;
            if (ownerId is null)
            {
                account = await accounts.FindOrCreateAsync(Input.Phone!, Input.Name!);
                ownerId = account.User.Id;
            }
            var (bookingId, _) = await workflow.ReceiveAsync(new IncomingBooking(
                "web-" + Input.SubmissionKey.ToString("N"), Selected!.Id, Input.Team!, Input.Name!, Input.Phone!,
                Input.Telegram, Input.Quantity!.Value, BookingSources.Web,
                OwnerUserId: ownerId, PrivacyConsentAt: DateTimeOffset.UtcNow), ct);
            if (account is { NewPassword: { } password })
            {
                await signIn.SignInAsync(account.User, isPersistent: true);
                TempData["NewLogin"] = account.User.UserName;
                TempData["NewPassword"] = password;     // shown once on «Мои заявки»
            }
            else if (account is not null)
            {
                TempData["Notice"] = "Этот номер уже зарегистрирован. Оплатите и загрузите чек здесь; заявку также видно в личном кабинете — вход по номеру телефона.";
                return RedirectToPage("/Pay", new { token = paymentLinks.Create(bookingId) });
            }
            return RedirectToPage("/Bookings/Mine", null, $"booking-{bookingId:N}");
        }
        catch (BusinessException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync(Input.SessionId, ct);
            return Page();
        }
    }

    /// <summary>Stale token: back to the form with the typed values and a fresh token; nothing was saved.</summary>
    private async Task<IActionResult> StaleAsync(CancellationToken ct)
    {
        ModelState.Clear();
        await LoadAsync(Input.SessionId, ct);
        if (Input.SubmissionKey == Guid.Empty) Input.SubmissionKey = Guid.NewGuid();
        ModelState.AddModelError(string.Empty, StaleForm);
        return Page();
    }

    private async Task LoadAsync(Guid? sessionId, CancellationToken ct)
    {
        Offers = await schedule.OpenAsync(ct);
        Selected = Offers.FirstOrDefault(x => x.Id == sessionId) ?? Offers.FirstOrDefault();
    }

    /// <summary>Server-side checks; normalises the phone and Telegram so the review shows what will be saved.</summary>
    private async Task<bool> ValidateAsync(CancellationToken ct)
    {
        await LoadAsync(Input.SessionId, ct);
        if (Selected is null || Selected.Id != Input.SessionId)
        {
            ModelState.AddModelError(string.Empty, "Запись на эту игру закрыта. Выберите другую дату.");
            Input.SessionId = Selected?.Id ?? Guid.Empty;
            return false;
        }
        if (Input.Quantity is { } q && (q < 1 || q > Selected.MaxQuantity))
            ModelState.AddModelError("Input.Quantity", Selected.Remaining == 0
                ? "Билеты на эту дату закончились." : $"Можно взять от 1 до {Selected.MaxQuantity} билетов.");
        if (!string.IsNullOrWhiteSpace(Input.Phone))
        {
            if (ContactRules.NormalizePhone(Input.Phone) is { } phone) Input.Phone = phone;
            else ModelState.AddModelError("Input.Phone", "Введите номер телефона, например +7 900 000-00-00.");
        }
        if (!string.IsNullOrWhiteSpace(Input.Telegram))
        {
            if (ContactRules.NormalizeTelegram(Input.Telegram) is { } nick) Input.Telegram = nick;
            else ModelState.AddModelError("Input.Telegram", "Укажите ник в формате @username (не почту и не ссылку на сайт).");
        }
        else Input.Telegram = null;
        Input.Name = Input.Name?.Trim();
        Input.Team = Input.Team?.Trim();
        if (Input.SubmissionKey == Guid.Empty) Input.SubmissionKey = Guid.NewGuid();
        return ModelState.IsValid;
    }
}
