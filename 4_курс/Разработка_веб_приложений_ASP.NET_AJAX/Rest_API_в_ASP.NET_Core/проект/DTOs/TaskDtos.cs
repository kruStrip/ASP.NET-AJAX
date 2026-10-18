using System.ComponentModel.DataAnnotations;
using TaskFlowApi.Entities;
using TaskFlowApi.Validation;

namespace TaskFlowApi.DTOs;

/// <summary>Данные задачи, возвращаемые клиенту (API v1).</summary>
public class TaskItemDto
{
    /// <summary>Идентификатор задачи.</summary>
    public int Id { get; set; }

    /// <summary>Заголовок задачи.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Описание задачи.</summary>
    public string? Description { get; set; }

    /// <summary>Статус задачи.</summary>
    public TaskItemStatus Status { get; set; }

    /// <summary>Идентификатор проекта.</summary>
    public int ProjectId { get; set; }

    /// <summary>Идентификатор исполнителя.</summary>
    public int? AssignedToId { get; set; }

    /// <summary>Имя исполнителя.</summary>
    public string? AssignedToUsername { get; set; }

    /// <summary>Срок выполнения.</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>Дата создания.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>Данные для создания задачи (API v1).</summary>
public class CreateTaskDto
{
    /// <summary>Заголовок задачи (минимум 5 символов).</summary>
    [Required]
    [StringLength(200, MinimumLength = 5)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Описание задачи.</summary>
    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>Статус задачи. Если не указан — используется ToDo.</summary>
    public TaskItemStatus? Status { get; set; }

    /// <summary>Идентификатор проекта, к которому относится задача.</summary>
    [Required]
    public int ProjectId { get; set; }

    /// <summary>Идентификатор исполнителя.</summary>
    public int? AssignedToId { get; set; }

    /// <summary>Срок выполнения (если указан — должен быть в будущем).</summary>
    [FutureDate]
    public DateTime? DueDate { get; set; }
}

/// <summary>Данные для полного обновления задачи (API v1).</summary>
public class UpdateTaskDto
{
    /// <summary>Заголовок задачи (минимум 5 символов).</summary>
    [Required]
    [StringLength(200, MinimumLength = 5)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Описание задачи.</summary>
    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>Статус задачи (обязателен при полной замене).</summary>
    [Required]
    public TaskItemStatus Status { get; set; }

    /// <summary>Идентификатор исполнителя.</summary>
    public int? AssignedToId { get; set; }

    /// <summary>Срок выполнения (если указан — должен быть в будущем).</summary>
    [FutureDate]
    public DateTime? DueDate { get; set; }
}
