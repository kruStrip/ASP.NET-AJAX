using System.ComponentModel.DataAnnotations;

namespace TaskFlowApi.DTOs;

/// <summary>Данные проекта, возвращаемые клиенту.</summary>
public class ProjectDto
{
    /// <summary>Идентификатор проекта.</summary>
    public int Id { get; set; }

    /// <summary>Название проекта.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Описание проекта.</summary>
    public string? Description { get; set; }

    /// <summary>Дата создания проекта.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Количество задач в проекте.</summary>
    public int TaskCount { get; set; }
}

/// <summary>Данные для создания проекта.</summary>
public class CreateProjectDto
{
    /// <summary>Название проекта (3-100 символов).</summary>
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Описание проекта.</summary>
    [StringLength(1000)]
    public string? Description { get; set; }
}

/// <summary>Данные для обновления проекта.</summary>
public class UpdateProjectDto
{
    /// <summary>Название проекта (3-100 символов).</summary>
    [Required]
    [StringLength(100, MinimumLength = 3)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Описание проекта.</summary>
    [StringLength(1000)]
    public string? Description { get; set; }
}
