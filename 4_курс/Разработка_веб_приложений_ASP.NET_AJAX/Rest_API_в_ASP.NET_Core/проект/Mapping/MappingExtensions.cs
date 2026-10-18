using TaskFlowApi.DTOs;
using TaskFlowApi.Entities;

namespace TaskFlowApi.Mapping;

/// <summary>Ручные extension-методы для маппинга Entity в DTO.</summary>
public static class MappingExtensions
{
    /// <summary>Преобразует проект в DTO. Количество задач должно быть посчитано заранее.</summary>
    public static ProjectDto ToDto(this Project project, int taskCount)
    {
        return new ProjectDto
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            CreatedAt = project.CreatedAt,
            TaskCount = taskCount
        };
    }

    /// <summary>Преобразует задачу в DTO API v1.</summary>
    public static TaskItemDto ToDto(this TaskItem task)
    {
        return new TaskItemDto
        {
            Id = task.Id,
            Title = task.Title,
            Description = task.Description,
            Status = task.Status,
            ProjectId = task.ProjectId,
            AssignedToId = task.AssignedToId,
            AssignedToUsername = task.AssignedTo?.Username,
            DueDate = task.DueDate,
            CreatedAt = task.CreatedAt
        };
    }

    /// <summary>Преобразует задачу в DTO API v2.</summary>
    public static TaskItemV2Dto ToV2Dto(this TaskItem task)
    {
        return new TaskItemV2Dto
        {
            Id = task.Id,
            Title = task.Title,
            Status = task.Status,
            Priority = task.Priority,
            ProjectId = task.ProjectId,
            AssignedToId = task.AssignedToId,
            AssignedToUsername = task.AssignedTo?.Username,
            DueDate = task.DueDate,
            CreatedAt = task.CreatedAt
        };
    }

    /// <summary>Преобразует комментарий в DTO.</summary>
    public static CommentDto ToDto(this Comment comment)
    {
        return new CommentDto
        {
            Id = comment.Id,
            TaskItemId = comment.TaskItemId,
            AuthorId = comment.AuthorId,
            AuthorUsername = comment.Author?.Username ?? string.Empty,
            Content = comment.Content,
            CreatedAt = comment.CreatedAt
        };
    }
}
