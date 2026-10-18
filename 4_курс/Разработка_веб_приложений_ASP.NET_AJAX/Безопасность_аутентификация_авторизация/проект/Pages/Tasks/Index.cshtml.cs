using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SecureTodo.Data;
using SecureTodo.Models;

namespace SecureTodo.Pages.Tasks;

public class IndexModel(ApplicationDbContext db, IAuthorizationService authorizationService) : PageModel
{
    public List<TaskItem> Items { get; private set; } = [];
    public async Task OnGetAsync() => Items = await db.Tasks.Include(t => t.User).OrderByDescending(t => t.CreatedAt).ToListAsync();

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var task = await db.Tasks.FindAsync(id);
        if (task is null) return NotFound();
        if (!(await authorizationService.AuthorizeAsync(User, task, "CanEditTask")).Succeeded) return Forbid();
        db.Tasks.Remove(task);
        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostMarkCompletedAsync(int id)
    {
        var task = await db.Tasks.FindAsync(id);
        if (task is null) return NotFound();
        if (!(await authorizationService.AuthorizeAsync(User, task, "CanEditTask")).Succeeded) return Forbid();
        task.IsCompleted = true;
        await db.SaveChangesAsync();
        return new JsonResult(new { task.Id, task.IsCompleted });
    }
}
