using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TaskFlowApi.Entities;

namespace TaskFlowApi.Authorization;

/// <summary>Проверяет права на конкретную задачу, а не только роль пользователя.</summary>
public sealed class CanEditTaskHandler : AuthorizationHandler<CanEditTaskRequirement, TaskItem>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CanEditTaskRequirement requirement,
        TaskItem task)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (context.User.IsInRole("Admin") ||
            (int.TryParse(userId, out var parsedUserId) && task.CreatedByUserId == parsedUserId))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
