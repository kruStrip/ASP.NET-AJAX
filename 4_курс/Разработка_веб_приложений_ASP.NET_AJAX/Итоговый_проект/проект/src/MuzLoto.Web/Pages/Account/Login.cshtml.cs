using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Account;

public class LoginModel(SignInManager<AppUser> signInManager, UserManager<AppUser> users) : PageModel
{
    [BindProperty] public LoginInput Input { get; set; } = new();
    public sealed class LoginInput
    {
        /// <summary>Clients from the booking form log in by phone; registered users and staff by e-mail.</summary>
        [Required(ErrorMessage = "Укажите телефон или e-mail.")] public string Login { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    }
    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid) return Page();
        var login = Input.Login.Trim();
        var account = login.Contains('@') ? await users.FindByEmailAsync(login)
            : ContactRules.NormalizePhone(login) is { } phone ? await users.FindByNameAsync(phone) : null;
        var result = account is null ? Microsoft.AspNetCore.Identity.SignInResult.Failed
            : await signInManager.PasswordSignInAsync(account.UserName!, Input.Password, false, true);
        if (result.Succeeded)
        {
            if (Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl!);
            var isStaff = account is not null && (await users.IsInRoleAsync(account, "Admin") ||
                await users.IsInRoleAsync(account, "Manager"));
            return RedirectToPage(isStaff ? "/Index" : "/Bookings/Mine");
        }
        ModelState.AddModelError(string.Empty,
            result.IsLockedOut ? "Вход заблокирован на 15 минут." : "Неверный телефон, e-mail или пароль.");
        return Page();
    }
}
