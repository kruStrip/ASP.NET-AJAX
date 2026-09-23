using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskFlowApi.Data;
using TaskFlowApi.DTOs;
using TaskFlowApi.Entities;
using TaskFlowApi.Exceptions;
using TaskFlowApi.Mapping;

namespace TaskFlowApi.Controllers;

/// <summary>Управление задачами (версия 1, устарела — используйте v2).</summary>
[ApiController]
[ApiVersion("1.0", Deprecated = true)]
[Route("api/v{version:apiVersion}/tasks")]
[Produces("application/json")]
public class TasksController : ControllerBase
{
    private readonly AppDbContext _db;

    public TasksController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Получить список всех задач.</summary>
    /// <response code="200">Список задач.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TaskItemDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TaskItemDto>>> GetAll()
    {
        var tasks = await _db.Tasks.Include(t => t.AssignedTo).ToListAsync();
        return Ok(tasks.Select(t => t.ToDto()));
    }

    /// <summary>Получить задачу по идентификатору.</summary>
    /// <param name="id">Идентификатор задачи.</param>
    /// <response code="200">Задача найдена.</response>
    /// <response code="404">Задача не найдена.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(TaskItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskItemDto>> GetById(int id)
    {
        var task = await _db.Tasks.Include(t => t.AssignedTo).FirstOrDefaultAsync(t => t.Id == id);
        if (task is null)
        {
            throw new NotFoundException($"Задача с id={id} не найдена.");
        }

        return Ok(task.ToDto());
    }

    /// <summary>Создать новую задачу.</summary>
    /// <param name="dto">Данные новой задачи. Если Status не указан — используется ToDo.</param>
    /// <response code="201">Задача создана.</response>
    /// <response code="400">Данные задачи не прошли валидацию.</response>
    [HttpPost]
    [ProducesResponseType(typeof(TaskItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TaskItemDto>> Create(CreateTaskDto dto)
    {
        if (!await _db.Projects.AnyAsync(p => p.Id == dto.ProjectId))
        {
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(dto.ProjectId)] = new[] { "Проект с указанным ProjectId не найден." }
            }));
        }

        var task = new TaskItem
        {
            Title = dto.Title,
            Description = dto.Description,
            Status = dto.Status ?? TaskItemStatus.ToDo,
            ProjectId = dto.ProjectId,
            AssignedToId = dto.AssignedToId,
            DueDate = dto.DueDate,
            CreatedAt = DateTime.UtcNow
        };

        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = task.Id, version = "1.0" }, task.ToDto());
    }

    /// <summary>Обновить задачу целиком.</summary>
    /// <param name="id">Идентификатор задачи.</param>
    /// <param name="dto">Новые данные задачи. Status обязателен при полной замене.</param>
    /// <response code="204">Задача обновлена.</response>
    /// <response code="404">Задача не найдена.</response>
    /// <response code="400">Данные задачи не прошли валидацию.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(int id, UpdateTaskDto dto)
    {
        var task = await _db.Tasks.FindAsync(id);
        if (task is null)
        {
            throw new NotFoundException($"Задача с id={id} не найдена.");
        }

        task.Title = dto.Title;
        task.Description = dto.Description;
        task.Status = dto.Status;
        task.AssignedToId = dto.AssignedToId;
        task.DueDate = dto.DueDate;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Удалить задачу.</summary>
    /// <param name="id">Идентификатор задачи.</param>
    /// <response code="204">Задача удалена.</response>
    /// <response code="404">Задача не найдена.</response>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var task = await _db.Tasks.FindAsync(id);
        if (task is null)
        {
            throw new NotFoundException($"Задача с id={id} не найдена.");
        }

        _db.Tasks.Remove(task);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
