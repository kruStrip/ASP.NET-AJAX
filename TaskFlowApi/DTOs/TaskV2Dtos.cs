using System.ComponentModel.DataAnnotations;
using TaskFlowApi.Entities;
using TaskFlowApi.Validation;

namespace TaskFlowApi.DTOs;

/// <summary>Данные задачи, возвращаемые клиенту (API v2). Description убран, добавлен Priority.</summary>
public class TaskItemV2Dto
{
    /// <summary>Идентификатор задачи.</summary>
    public int Id { get; set; }

    /// <summary>Заголовок задачи.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Статус задачи.</summary>
    public TaskItemStatus Status { get; set; }

    /// <summary>Приоритет задачи.</summary>
    public TaskPriority Priority { get; set; }

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

/// <summary>Данные для создания задачи (API v2).</summary>
public class CreateTaskV2Dto
{
    /// <summary>Заголовок задачи (минимум 5 символов).</summary>
    [Required]
    [StringLength(200, MinimumLength = 5)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Статус задачи. Если не указан — используется ToDo.</summary>
    public TaskItemStatus? Status { get; set; }

    /// <summary>Приоритет задачи. Если не указан — используется Medium.</summary>
    public TaskPriority? Priority { get; set; }

    /// <summary>Идентификатор проекта, к которому относится задача.</summary>
    [Required]
    public int ProjectId { get; set; }

    /// <summary>Идентификатор исполнителя.</summary>
    public int? AssignedToId { get; set; }

    /// <summary>Срок выполнения (если указан — должен быть в будущем).</summary>
    [FutureDate]
    public DateTime? DueDate { get; set; }
}

/// <summary>Данные для полного обновления задачи (API v2).</summary>
public class UpdateTaskV2Dto
{
    /// <summary>Заголовок задачи (минимум 5 символов).</summary>
    [Required]
    [StringLength(200, MinimumLength = 5)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Статус задачи (обязателен при полной замене).</summary>
    [Required]
    public TaskItemStatus Status { get; set; }

    /// <summary>Приоритет задачи (обязателен при полной замене).</summary>
    [Required]
    public TaskPriority Priority { get; set; }

    /// <summary>Идентификатор исполнителя.</summary>
    public int? AssignedToId { get; set; }

    /// <summary>Срок выполнения (если указан — должен быть в будущем).</summary>
    [FutureDate]
    public DateTime? DueDate { get; set; }
}

/// <summary>Данные для смены статуса задачи.</summary>
public class UpdateTaskStatusDto
{
    /// <summary>Новый статус задачи.</summary>
    [Required]
    public TaskItemStatus Status { get; set; }
}
