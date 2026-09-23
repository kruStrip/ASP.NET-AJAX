using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskFlowApi.Data;
using TaskFlowApi.DTOs;
using TaskFlowApi.Entities;
using TaskFlowApi.Exceptions;
using TaskFlowApi.Mapping;

namespace TaskFlowApi.Controllers;

/// <summary>Управление проектами.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ProjectsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ProjectsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Получить список всех проектов.</summary>
    /// <response code="200">Список проектов.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ProjectDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ProjectDto>>> GetAll()
    {
        var projects = await _db.Projects
            .Select(p => new ProjectDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                CreatedAt = p.CreatedAt,
                TaskCount = p.Tasks.Count
            })
            .ToListAsync();

        return Ok(projects);
    }

    /// <summary>Получить проект по идентификатору.</summary>
    /// <param name="id">Идентификатор проекта.</param>
    /// <response code="200">Проект найден.</response>
    /// <response code="404">Проект не найден.</response>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectDto>> GetById(int id)
    {
        var project = await _db.Projects
            .Where(p => p.Id == id)
            .Select(p => new ProjectDto
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                CreatedAt = p.CreatedAt,
                TaskCount = p.Tasks.Count
            })
            .FirstOrDefaultAsync();

        if (project is null)
        {
            throw new NotFoundException($"Проект с id={id} не найден.");
        }

        return Ok(project);
    }

    /// <summary>Создать новый проект.</summary>
    /// <param name="dto">Данные нового проекта.</param>
    /// <response code="201">Проект создан.</response>
    /// <response code="400">Данные проекта не прошли валидацию.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectDto dto)
    {
        var project = new Project
        {
            Name = dto.Name,
            Description = dto.Description,
            CreatedAt = DateTime.UtcNow
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        var result = project.ToDto(0);
        return CreatedAtAction(nameof(GetById), new { id = project.Id }, result);
    }

    /// <summary>Обновить проект целиком.</summary>
    /// <param name="id">Идентификатор проекта.</param>
    /// <param name="dto">Новые данные проекта.</param>
    /// <response code="204">Проект обновлён.</response>
    /// <response code="404">Проект не найден.</response>
    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(int id, UpdateProjectDto dto)
    {
        var project = await _db.Projects.FindAsync(id);
        if (project is null)
        {
            throw new NotFoundException($"Проект с id={id} не найден.");
        }

        project.Name = dto.Name;
        project.Description = dto.Description;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Удалить проект вместе с его задачами.</summary>
    /// <param name="id">Идентификатор проекта.</param>
    /// <response code="204">Проект удалён.</response>
    /// <response code="404">Проект не найден.</response>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var project = await _db.Projects.FindAsync(id);
        if (project is null)
        {
            throw new NotFoundException($"Проект с id={id} не найден.");
        }

        _db.Projects.Remove(project);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
