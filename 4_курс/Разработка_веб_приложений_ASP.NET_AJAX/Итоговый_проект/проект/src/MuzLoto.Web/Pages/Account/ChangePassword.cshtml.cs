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
        [Required, DataType(DataType.Password)] public string CurrentPassword { get; set; } = "";
        [Required, MinLength(12), DataType(DataType.Password)] public string NewPassword { get; set; } = "";
        [Required, Compare(nameof(NewPassword)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    }
    public IActionResult OnGet() => Page();
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var result = await users.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }
        await signIn.RefreshSignInAsync(user);
        TempData["Notice"] = "Пароль изменён. Ранее выданные JWT-токены недействительны.";
        return RedirectToPage("/Home");
    }
}
