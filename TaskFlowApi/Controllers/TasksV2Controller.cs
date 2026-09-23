using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskFlowApi.Data;
using TaskFlowApi.DTOs;
using TaskFlowApi.Entities;
using TaskFlowApi.Exceptions;
using TaskFlowApi.Mapping;

namespace TaskFlowApi.Controllers;

/// <summary>Управление задачами (версия 2: приоритет, пагинация, фильтрация, идемпотентность).</summary>
[ApiController]
[ApiVersion("2.0")]
[Route("api/v{version:apiVersion}/tasks")]
[Produces("application/json")]
public class TasksV2Controller : ControllerBase
{
    private const int MaxPageSize = 50;

    private readonly AppDbContext _db;

    public TasksV2Controller(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Получить список задач с пагинацией, фильтрацией, поиском и сортировкой.</summary>
    /// <param name="page">Номер страницы (по умолчанию 1).</param>
    /// <param name="pageSize">Размер страницы (по умолчанию 10, максимум 50).</param>
    /// <param name="status">Фильтр по статусу (ToDo, InProgress, Done).</param>
    /// <param name="priority">Фильтр по приоритету (Low, Medium, High).</param>
    /// <param name="sortBy">Поле для сортировки (title, dueDate, createdAt).</param>
    /// <param name="sortDir">Направление сортировки (asc, desc).</param>
    /// <param name="search">Поиск подстроки в заголовке задачи.</param>
    /// <response code="200">Страница задач.</response>
    /// <response code="400">pageSize превышает 50.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<TaskItemV2Dto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<TaskItemV2Dto>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? status = null,
        [FromQuery] string? priority = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] string? search = null)
    {
        if (pageSize > MaxPageSize)
        {
            return ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                [nameof(pageSize)] = new[] { $"pageSize не может превышать {MaxPageSize}." }
            }));
        }

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 10;

        var query = _db.Tasks.Include(t => t.AssignedTo).AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<TaskItemStatus>(status, true, out var statusValue))
        {
            query = query.Where(t => t.Status == statusValue);
        }

        if (!string.IsNullOrWhiteSpace(priority) && Enum.TryParse<TaskPriority>(priority, true, out var priorityValue))
        {
            query = query.Where(t => t.Priority == priorityValue);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t => t.Title.Contains(search));
        }

        var descending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);

        query = sortBy?.ToLowerInvariant() switch
        {
            "title" => descending ? query.OrderByDescending(t => t.Title) : query.OrderBy(t => t.Title),
            "duedate" => descending ? query.OrderByDescending(t => t.DueDate) : query.OrderBy(t => t.DueDate),
            "createdat" => descending ? query.OrderByDescending(t => t.CreatedAt) : query.OrderBy(t => t.CreatedAt),
            _ => query.OrderBy(t => t.Id)
        };

        var totalCount = await query.CountAsync();

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var result = new PagedResult<TaskItemV2Dto>
        {
            Items = items.Select(t => t.ToV2Dto()).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };

        return Ok(result);
    }

    /// <summary>Получить задачу по идентификатору.</summary>
    /// <param name="id">Идентификатор задачи.</param>
    /// <response code="200">Задача найдена.</response>
    /// <response code="404">Задача не найдена.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(TaskItemV2Dto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskItemV2Dto>> GetById(int id)
    {
        var task = await _db.Tasks.Include(t => t.AssignedTo).FirstOrDefaultAsync(t => t.Id == id);
        if (task is null)
        {
            throw new NotFoundException($"Задача с id={id} не найдена.");
        }

        return Ok(task.ToV2Dto());
    }

    /// <summary>
    /// Создать новую задачу. Требует заголовок X-Idempotency-Key
    /// (проверяется в <see cref="Middleware.IdempotencyMiddleware"/>).
    /// </summary>
    /// <param name="dto">Данные новой задачи.</param>
    /// <response code="201">Задача создана.</response>
    /// <response code="400">Данные задачи не прошли валидацию либо отсутствует X-Idempotency-Key.</response>
    /// <response code="409">Idempotency-Key уже использован с другим телом запроса.</response>
    [HttpPost]
    [ProducesResponseType(typeof(TaskItemV2Dto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TaskItemV2Dto>> Create(CreateTaskV2Dto dto)
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
            Status = dto.Status ?? TaskItemStatus.ToDo,
            Priority = dto.Priority ?? TaskPriority.Medium,
            ProjectId = dto.ProjectId,
            AssignedToId = dto.AssignedToId,
            DueDate = dto.DueDate,
            CreatedAt = DateTime.UtcNow
        };

        _db.Tasks.Add(task);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = task.Id, version = "2.0" }, task.ToV2Dto());
    }

    /// <summary>Обновить задачу целиком.</summary>
    /// <param name="id">Идентификатор задачи.</param>
    /// <param name="dto">Новые данные задачи.</param>
    /// <response code="204">Задача обновлена.</response>
    /// <response code="404">Задача не найдена.</response>
    /// <response code="400">Данные задачи не прошли валидацию.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(int id, UpdateTaskV2Dto dto)
    {
        var task = await _db.Tasks.FindAsync(id);
        if (task is null)
        {
            throw new NotFoundException($"Задача с id={id} не найдена.");
        }

        task.Title = dto.Title;
        task.Status = dto.Status;
        task.Priority = dto.Priority;
        task.AssignedToId = dto.AssignedToId;
        task.DueDate = dto.DueDate;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Изменить статус задачи. Завершить задачу без комментариев нельзя.</summary>
    /// <param name="id">Идентификатор задачи.</param>
    /// <param name="dto">Новый статус.</param>
    /// <response code="204">Статус обновлён.</response>
    /// <response code="404">Задача не найдена.</response>
    /// <response code="422">Нарушено бизнес-правило (нельзя завершить задачу без комментариев).</response>
    [HttpPatch("{id:int}/status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateStatus(int id, UpdateTaskStatusDto dto)
    {
        var task = await _db.Tasks.Include(t => t.Comments).FirstOrDefaultAsync(t => t.Id == id);
        if (task is null)
        {
            throw new NotFoundException($"Задача с id={id} не найдена.");
        }

        if (dto.Status == TaskItemStatus.Done && task.Comments.Count == 0)
        {
            throw new BusinessRuleException("Нельзя завершить задачу без комментариев");
        }

        task.Status = dto.Status;
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
