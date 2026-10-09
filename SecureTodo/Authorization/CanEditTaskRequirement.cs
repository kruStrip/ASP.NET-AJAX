using Microsoft.AspNetCore.Authorization;
using SecureTodo.Models;
using System.Security.Claims;

namespace SecureTodo.Authorization;

public sealed class CanEditTaskRequirement : IAuthorizationRequirement;

public sealed class CanEditTaskHandler : AuthorizationHandler<CanEditTaskRequirement, TaskItem>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CanEditTaskRequirement requirement,
        TaskItem resource)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (context.User.IsInRole("Admin") || userId == resource.UserId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
