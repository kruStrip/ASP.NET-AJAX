using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages;

public class HomeModel(UserManager<AppUser> users) : PageModel
{
    public string? DisplayName { get; private set; }

    public async Task OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated != true) return;
        var user = await users.GetUserAsync(User);
        DisplayName = string.IsNullOrWhiteSpace(user?.FullName) ? User.Identity.Name : user.FullName;
    }
}
