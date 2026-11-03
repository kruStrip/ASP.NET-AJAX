using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Account;

/// <summary>
/// Only staff can change a password here. Customer accounts are temporary (see <c>ClientAccounts</c>): they are
/// deleted after the payment is confirmed, so there is nothing to protect with an own password.
/// </summary>
[Authorize(Roles = "Admin,Manager")]
public sealed class ChangePasswordModel(UserManager<AppUser> users, SignInManager<AppUser> signIn) : PageModel
{
    public const int MinLength = 12;

    [BindProperty] public InputModel Input { get; set; } = new();
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Введите текущий пароль."), DataType(DataType.Password)] public string CurrentPassword { get; set; } = "";
        [Required(ErrorMessage = "Введите новый пароль."), DataType(DataType.Password)] public string NewPassword { get; set; } = "";
        [Required(ErrorMessage = "Повторите новый пароль."), Compare(nameof(NewPassword), ErrorMessage = "Пароли не совпадают."), DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = "";
    }

    public IActionResult OnGet() => Page();
    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.NewPassword.Length < MinLength)
            ModelState.AddModelError("Input.NewPassword", $"Новый пароль: не короче {MinLength} символов.");
        if (!Input.NewPassword.Any(char.IsDigit))
            ModelState.AddModelError("Input.NewPassword", "В новом пароле нужна хотя бы одна цифра.");
        if (!ModelState.IsValid) return Page();
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var result = await users.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Code == "PasswordMismatch" ? "Текущий пароль указан неверно." : error.Description);
            return Page();
        }
        await signIn.RefreshSignInAsync(user);
        TempData["Notice"] = "Пароль изменён.";
        return RedirectToPage("/Index");
    }
}
