using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskFlowApi.Data;
using TaskFlowApi.DTOs;
using TaskFlowApi.Entities;
using TaskFlowApi.Exceptions;
using TaskFlowApi.Mapping;

namespace TaskFlowApi.Controllers;

/// <summary>Работа с комментариями задач.</summary>
[ApiController]
[Route("api")]
[Produces("application/json")]
public class CommentsController : ControllerBase
{
    private readonly AppDbContext _db;

    public CommentsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Получить список комментариев задачи.</summary>
    /// <param name="taskId">Идентификатор задачи.</param>
    /// <response code="200">Список комментариев.</response>
    /// <response code="404">Задача не найдена.</response>
    [HttpGet("tasks/{taskId:int}/comments")]
    [ProducesResponseType(typeof(IEnumerable<CommentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<CommentDto>>> GetForTask(int taskId)
    {
        if (!await _db.Tasks.AnyAsync(t => t.Id == taskId))
        {
            throw new NotFoundException($"Задача с id={taskId} не найдена.");
        }

        var comments = await _db.Comments
            .Include(c => c.Author)
            .Where(c => c.TaskItemId == taskId)
            .ToListAsync();

        return Ok(comments.Select(c => c.ToDto()));
    }

    /// <summary>Добавить комментарий к задаче.</summary>
    /// <param name="taskId">Идентификатор задачи.</param>
    /// <param name="dto">Данные комментария.</param>
    /// <response code="201">Комментарий создан.</response>
    /// <response code="404">Задача не найдена.</response>
    /// <response code="400">Данные комментария не прошли валидацию.</response>
    [HttpPost("tasks/{taskId:int}/comments")]
    [ProducesResponseType(typeof(CommentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CommentDto>> Create(int taskId, CreateCommentDto dto)
    {
        if (!await _db.Tasks.AnyAsync(t => t.Id == taskId))
        {
            throw new NotFoundException($"Задача с id={taskId} не найдена.");
        }

        var comment = new Comment
        {
            TaskItemId = taskId,
            AuthorId = dto.AuthorId,
            Content = dto.Content,
            CreatedAt = DateTime.UtcNow
        };

        _db.Comments.Add(comment);
        await _db.SaveChangesAsync();

        await _db.Entry(comment).Reference(c => c.Author).LoadAsync();

        return CreatedAtAction(nameof(GetForTask), new { taskId }, comment.ToDto());
    }

    /// <summary>Удалить комментарий.</summary>
    /// <param name="id">Идентификатор комментария.</param>
    /// <response code="204">Комментарий удалён.</response>
    /// <response code="404">Комментарий не найден.</response>
    [HttpDelete("comments/{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var comment = await _db.Comments.FindAsync(id);
        if (comment is null)
        {
            throw new NotFoundException($"Комментарий с id={id} не найден.");
        }

        _db.Comments.Remove(comment);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
