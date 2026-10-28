using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Account;

public class LoginModel(SignInManager<AppUser> signInManager, UserManager<AppUser> users) : PageModel
{
    [BindProperty] public LoginInput Input { get; set; } = new();
    public sealed class LoginInput
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    }
    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid) return Page();
        var result = await signInManager.PasswordSignInAsync(Input.Email, Input.Password, false, true);
        if (result.Succeeded)
        {
            var account = await users.FindByEmailAsync(Input.Email);
            if (account is not null && !await users.IsInRoleAsync(account, "Admin") &&
                !await users.IsInRoleAsync(account, "Manager"))
                return RedirectToPage("/Account/ChangePassword");
            return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Page("/Index")!);
        }
        ModelState.AddModelError(string.Empty,
            result.IsLockedOut ? "Вход заблокирован на 15 минут." : "Неверный e-mail или пароль.");
        return Page();
    }
}
