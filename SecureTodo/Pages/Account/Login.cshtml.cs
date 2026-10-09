using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SecureTodo.Models;
using System.ComponentModel.DataAnnotations;

namespace SecureTodo.Pages.Account;

public class LoginModel(SignInManager<AppUser> signInManager) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required, EmailAddress, Display(Name = "E-mail")] public string Email { get; set; } = string.Empty;
        [Required, DataType(DataType.Password), Display(Name = "Пароль")] public string Password { get; set; } = string.Empty;
        [Display(Name = "Запомнить меня")] public bool RememberMe { get; set; }
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (!ModelState.IsValid) return Page();
        var result = await signInManager.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(returnUrl ?? Url.Page("/Index")!);
        if (result.RequiresTwoFactor) return RedirectToPage("TfaLogin", new { returnUrl, Input.RememberMe });
        ModelState.AddModelError(string.Empty, result.IsLockedOut ? "Аккаунт заблокирован на 15 минут." : "Неверный e-mail или пароль.");
        return Page();
    }
}
