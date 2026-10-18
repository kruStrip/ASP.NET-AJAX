using System.ComponentModel.DataAnnotations;

namespace TaskFlowApi.Entities;

/// <summary>
/// Проект, объединяющий набор задач.
/// </summary>
public class Project
{
    /// <summary>Идентификатор проекта.</summary>
    public int Id { get; set; }

    /// <summary>Название проекта.</summary>
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Описание проекта.</summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>Дата создания проекта.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Задачи проекта.</summary>
    public List<TaskItem> Tasks { get; set; } = new();
}
