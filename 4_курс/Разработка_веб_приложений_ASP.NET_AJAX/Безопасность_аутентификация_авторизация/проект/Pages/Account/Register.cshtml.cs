using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SecureTodo.Models;
using System.ComponentModel.DataAnnotations;

namespace SecureTodo.Pages.Account;

public class RegisterModel(UserManager<AppUser> userManager, SignInManager<AppUser> signInManager) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public class InputModel
    {
        [Required, StringLength(100), Display(Name = "Полное имя")]
        public string FullName { get; set; } = string.Empty;
        [Required, EmailAddress, Display(Name = "E-mail")]
        public string Email { get; set; } = string.Empty;
        [Required, DataType(DataType.Password), StringLength(100, MinimumLength = 6), Display(Name = "Пароль")]
        public string Password { get; set; } = string.Empty;
        [Required, DataType(DataType.Password), Compare(nameof(Password)), Display(Name = "Повторите пароль")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        var user = new AppUser { UserName = Input.Email, Email = Input.Email, FullName = Input.FullName, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, Input.Password);
        if (result.Succeeded)
        {
            var roleResult = await userManager.AddToRoleAsync(user, "User");
            if (roleResult.Succeeded)
            {
                await signInManager.SignInAsync(user, isPersistent: false);
                return RedirectToPage("/Index");
            }
            foreach (var error in roleResult.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }
        foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
        return Page();
    }
}
