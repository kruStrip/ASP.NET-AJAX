using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.PaymentSettings;

/// <summary>What customers see after «К оплате»: transfer details text and a QR code.</summary>
[Authorize(Roles = "Admin")]
public class IndexModel(AppDbContext db, CoverImages images) : PageModel
{
    public Domain.PaymentSettings Settings { get; private set; } = new();

    [BindProperty, StringLength(2000, ErrorMessage = "Текст: не длиннее 2000 символов.")]
    public string? Instructions { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Settings = await images.PaymentSettingsAsync(ct);
        Instructions = Settings.Instructions;
    }

    public async Task<IActionResult> OnPostTextAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) { Settings = await images.PaymentSettingsAsync(ct); return Page(); }
        var settings = await images.PaymentSettingsAsync(ct);
        settings.Instructions = string.IsNullOrWhiteSpace(Instructions) ? null : Instructions.Trim();
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Done("Текст для оплаты сохранён.");
    }

    public async Task<IActionResult> OnPostQrAsync(IFormFile? image, CancellationToken ct)
    {
        if (image is null) return Done("Выберите файл с QR-кодом.", error: true);
        try { await images.SavePaymentQrAsync(image, ct); }
        catch (BusinessException ex) { return Done(ex.Message, error: true); }
        return Done("QR-код сохранён.");
    }

    public async Task<IActionResult> OnPostDeleteQrAsync(CancellationToken ct)
    {
        await images.DeletePaymentQrAsync(ct);
        return Done("QR-код удалён.");
    }

    private IActionResult Done(string message, bool error = false)
    {
        TempData[error ? "Error" : "Notice"] = message;
        return RedirectToPage();
    }
}
