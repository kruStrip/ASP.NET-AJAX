using System.ComponentModel.DataAnnotations;

namespace TaskFlowApi.DTOs;

/// <summary>Данные комментария, возвращаемые клиенту.</summary>
public class CommentDto
{
    /// <summary>Идентификатор комментария.</summary>
    public int Id { get; set; }

    /// <summary>Идентификатор задачи.</summary>
    public int TaskItemId { get; set; }

    /// <summary>Идентификатор автора.</summary>
    public int AuthorId { get; set; }

    /// <summary>Имя автора комментария.</summary>
    public string AuthorUsername { get; set; } = string.Empty;

    /// <summary>Текст комментария.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Дата создания комментария.</summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>Данные для создания комментария.</summary>
public class CreateCommentDto
{
    /// <summary>Идентификатор автора комментария.</summary>
    [Required]
    public int AuthorId { get; set; }

    /// <summary>Текст комментария (1-500 символов).</summary>
    [Required]
    [StringLength(500, MinimumLength = 1)]
    public string Content { get; set; } = string.Empty;
}
