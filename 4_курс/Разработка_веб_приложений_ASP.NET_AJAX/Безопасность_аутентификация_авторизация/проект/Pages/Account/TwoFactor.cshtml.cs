using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QRCoder;
using SecureTodo.Models;
using System.ComponentModel.DataAnnotations;

namespace SecureTodo.Pages.Account;

[Authorize]
public class TwoFactorModel(UserManager<AppUser> userManager) : PageModel
{
    [BindProperty, Required, StringLength(7, MinimumLength = 6), Display(Name = "Код подтверждения")]
    public string Code { get; set; } = string.Empty;
    public bool IsEnabled { get; private set; }
    public string SharedKey { get; private set; } = string.Empty;
    public string QrCodeDataUri { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        await LoadAsync(user, resetWhenMissing: true);
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var code = Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var valid = await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code);
        if (!valid)
        {
            ModelState.AddModelError(nameof(Code), "Неверный код.");
            await LoadAsync(user, resetWhenMissing: false);
            return Page();
        }
        await userManager.SetTwoFactorEnabledAsync(user, true);
        await userManager.UpdateSecurityStampAsync(user);
        TempData["Status"] = "Двухфакторная аутентификация включена.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        await userManager.SetTwoFactorEnabledAsync(user, false);
        await userManager.ResetAuthenticatorKeyAsync(user);
        await userManager.UpdateSecurityStampAsync(user);
        TempData["Status"] = "Двухфакторная аутентификация отключена.";
        return RedirectToPage();
    }

    private async Task LoadAsync(AppUser user, bool resetWhenMissing)
    {
        IsEnabled = await userManager.GetTwoFactorEnabledAsync(user);
        if (IsEnabled) return;
        SharedKey = await userManager.GetAuthenticatorKeyAsync(user) ?? string.Empty;
        if (string.IsNullOrEmpty(SharedKey) && resetWhenMissing)
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            SharedKey = await userManager.GetAuthenticatorKeyAsync(user) ?? string.Empty;
        }
        var email = Uri.EscapeDataString(user.Email ?? user.UserName ?? "user");
        var issuer = Uri.EscapeDataString("SecureTodo");
        var uri = $"otpauth://totp/{issuer}:{email}?secret={SharedKey}&issuer={issuer}&digits=6";
        using var data = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        QrCodeDataUri = $"data:image/png;base64,{Convert.ToBase64String(qr.GetGraphic(8))}";
    }
}
