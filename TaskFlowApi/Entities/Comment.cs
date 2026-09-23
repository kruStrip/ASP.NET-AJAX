using System.ComponentModel.DataAnnotations;

namespace TaskFlowApi.Entities;

/// <summary>
/// Комментарий к задаче.
/// </summary>
public class Comment
{
    /// <summary>Идентификатор комментария.</summary>
    public int Id { get; set; }

    /// <summary>Идентификатор задачи, к которой относится комментарий.</summary>
    public int TaskItemId { get; set; }

    /// <summary>Задача, к которой относится комментарий.</summary>
    public TaskItem? TaskItem { get; set; }

    /// <summary>Идентификатор автора комментария.</summary>
    public int AuthorId { get; set; }

    /// <summary>Автор комментария.</summary>
    public AppUser? Author { get; set; }

    /// <summary>Текст комментария.</summary>
    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Дата создания комментария.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
