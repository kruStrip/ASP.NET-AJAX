using Microsoft.AspNetCore.Authorization;

namespace TaskFlowApi.Authorization;

/// <summary>Требует, чтобы задачу редактировал её автор или администратор.</summary>
public sealed class CanEditTaskRequirement : IAuthorizationRequirement
{
}
