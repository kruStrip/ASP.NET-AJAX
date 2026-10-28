using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Account;

public class LogoutModel(SignInManager<AppUser> signInManager) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Home");
    public async Task<IActionResult> OnPostAsync()
    {
        await signInManager.SignOutAsync();
        return RedirectToPage("/Home");
    }
}
