using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Account;

[Authorize]
public sealed class ChangePasswordModel(UserManager<AppUser> users, SignInManager<AppUser> signIn) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Введите текущий пароль."), DataType(DataType.Password)] public string CurrentPassword { get; set; } = "";
        [Required(ErrorMessage = "Введите новый пароль."), DataType(DataType.Password)] public string NewPassword { get; set; } = "";
        [Required(ErrorMessage = "Повторите новый пароль."), Compare(nameof(NewPassword), ErrorMessage = "Пароли не совпадают."), DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = "";
    }
    /// <summary>Staff accounts need a long password; customers the same rule as at registration (6+, with a digit).</summary>
    public bool IsStaff => User.IsInRole("Admin") || User.IsInRole("Manager");
    public int MinLength => IsStaff ? 12 : 6;

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
        return RedirectToPage(IsStaff ? "/Index" : "/Bookings/Mine");
    }
}
