using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SecureTodo.Models;
using System.ComponentModel.DataAnnotations;

namespace SecureTodo.Pages.Account;

public class TfaLoginModel(SignInManager<AppUser> signInManager) : PageModel
{
    [BindProperty, Required, StringLength(7, MinimumLength = 6), Display(Name = "Код")]
    public string Code { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public bool RememberMe { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        return await signInManager.GetTwoFactorAuthenticationUserAsync() is null
            ? RedirectToPage("Login") : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
            Code.Replace(" ", string.Empty).Replace("-", string.Empty), RememberMe, rememberClient: false);
        if (result.Succeeded) return LocalRedirect(ReturnUrl ?? Url.Page("/Index")!);
        if (result.IsLockedOut) return RedirectToPage("Login");
        ModelState.AddModelError(string.Empty, "Неверный код аутентификатора.");
        return Page();
    }
}
