using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureTodo.Data;
using SecureTodo.DTOs;
using SecureTodo.Models;
using System.Security.Claims;

namespace SecureTodo.Controllers;

[ApiController]
[Route("api/tasks")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class TasksApiController(ApplicationDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TaskItem>>> GetAll()
    {
        var query = db.Tasks.AsNoTracking().Include(t => t.User).AsQueryable();
        if (!User.IsInRole("Admin"))
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            query = query.Where(t => t.UserId == userId);
        }
        return Ok(await query.OrderByDescending(t => t.CreatedAt).Select(t => new
        {
            t.Id, t.Title, t.Description, t.CreatedAt, t.IsCompleted, t.UserId,
            Author = t.User!.FullName
        }).ToListAsync());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        if (task is null) return NotFound();
        if (!CanEdit(task)) return Forbid();
        return Ok(task);
    }

    [HttpPost]
    public async Task<IActionResult> Create(TaskRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var task = new TaskItem
        {
            Title = request.Title,
            Description = request.Description,
            IsCompleted = request.IsCompleted,
            CreatedAt = DateTime.UtcNow,
            UserId = userId
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = task.Id }, task);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, TaskRequest request)
    {
        var task = await db.Tasks.FindAsync(id);
        if (task is null) return NotFound();
        if (!CanEdit(task)) return Forbid();
        task.Title = request.Title;
        task.Description = request.Description;
        task.IsCompleted = request.IsCompleted;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var task = await db.Tasks.FindAsync(id);
        if (task is null) return NotFound();
        if (!CanEdit(task)) return Forbid();
        db.Tasks.Remove(task);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private bool CanEdit(TaskItem task) =>
        User.IsInRole("Admin") || task.UserId == User.FindFirstValue(ClaimTypes.NameIdentifier);
}
