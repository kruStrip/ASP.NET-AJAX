using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SecureTodo.Data;
using SecureTodo.Models;

namespace SecureTodo.Pages.Tasks;

[Authorize]
public class EditModel(ApplicationDbContext db, IAuthorizationService authorizationService) : PageModel
{
    [BindProperty] public TaskItem Item { get; set; } = new();
    public async Task<IActionResult> OnGetAsync(int id)
    {
        var task = await db.Tasks.FindAsync(id);
        if (task is null) return NotFound();
        if (!(await authorizationService.AuthorizeAsync(User, task, "CanEditTask")).Succeeded) return Forbid();
        Item = task;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (id != Item.Id) return BadRequest();
        var task = await db.Tasks.FindAsync(id);
        if (task is null) return NotFound();
        if (!(await authorizationService.AuthorizeAsync(User, task, "CanEditTask")).Succeeded) return Forbid();
        ModelState.Remove("Item.UserId");
        if (!ModelState.IsValid) return Page();
        task.Title = Item.Title;
        task.Description = Item.Description;
        task.IsCompleted = Item.IsCompleted;
        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }
}
