using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using SecureTodo.Models;

namespace SecureTodo.Pages;

public class IndexModel(UserManager<AppUser> userManager) : PageModel
{
    public string? FullName { get; private set; }

    public async Task OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
            FullName = (await userManager.GetUserAsync(User))?.FullName;
    }
}
