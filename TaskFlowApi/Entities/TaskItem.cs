using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TaskFlowApi.Entities;

/// <summary>
/// Задача, относящаяся к проекту.
/// </summary>
public class TaskItem
{
    /// <summary>Идентификатор задачи.</summary>
    public int Id { get; set; }

    /// <summary>Идентификатор проекта, к которому относится задача.</summary>
    public int ProjectId { get; set; }

    /// <summary>Проект, к которому относится задача.</summary>
    public Project? Project { get; set; }

    /// <summary>Заголовок задачи.</summary>
    [Required]
    [StringLength(200, MinimumLength = 5)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Описание задачи.</summary>
    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>Статус задачи.</summary>
    public TaskItemStatus Status { get; set; } = TaskItemStatus.ToDo;

    /// <summary>Приоритет задачи (используется в API v2).</summary>
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    /// <summary>Идентификатор пользователя, на которого назначена задача.</summary>
    public int? AssignedToId { get; set; }

    /// <summary>Пользователь, на которого назначена задача.</summary>
    public AppUser? AssignedTo { get; set; }

    /// <summary>Срок выполнения задачи.</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>Дата создания задачи.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Комментарии к задаче.</summary>
    public List<Comment> Comments { get; set; } = new();
}
