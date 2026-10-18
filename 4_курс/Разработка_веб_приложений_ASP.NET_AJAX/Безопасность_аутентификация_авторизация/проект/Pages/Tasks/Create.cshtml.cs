using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SecureTodo.Data;
using SecureTodo.Models;

namespace SecureTodo.Pages.Tasks;

[Authorize]
public class CreateModel(ApplicationDbContext db, UserManager<AppUser> userManager) : PageModel
{
    [BindProperty] public TaskItem Item { get; set; } = new();
    public async Task<IActionResult> OnPostAsync()
    {
        ModelState.Remove("Item.UserId");
        if (!ModelState.IsValid) return Page();
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        Item.UserId = user.Id;
        Item.CreatedAt = DateTime.UtcNow;
        Item.Id = 0;
        db.Tasks.Add(Item);
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }
}
