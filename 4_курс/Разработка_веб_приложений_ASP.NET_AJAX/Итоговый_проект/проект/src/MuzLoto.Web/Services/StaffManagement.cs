using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

/// <summary>Administrative operations on staff and game assignments. Never accepts a caller-supplied role outside the allowlist.</summary>
public sealed class StaffManagement(AppDbContext db, UserManager<AppUser> users, IConfiguration config)
{
    public static readonly string[] Roles = ["Admin", "Manager", "User"];
    public static bool IsAllowedRole(string? role) => role is "Admin" or "Manager" or "User";

    public async Task<AppUser> CreateAsync(string fullName, string email, string initialPassword,
        string role, CancellationToken ct = default)
    {
        if (!IsAllowedRole(role)) throw new BusinessException("Неизвестная роль.", 400);
        fullName = fullName.Trim();
        email = email.Trim();
        if (fullName.Length is < 2 or > 150 || email.Length is < 3 or > 254 ||
            initialPassword.Length < 12)
            throw new BusinessException("Проверьте имя, почту и временный пароль (от 12 символов).", 400);
        if (await users.FindByEmailAsync(email) is not null)
            throw new BusinessException("Сотрудник с таким e-mail уже зарегистрирован.", 409);

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = new AppUser
        {
            FullName = fullName, Email = email, UserName = email,
            // Accounts are provisioned by an administrator: no email confirmation workflow exists.
            EmailConfirmed = true
        };
        RequireSuccess(await users.CreateAsync(user, initialPassword), "Создание сотрудника");
        RequireSuccess(await users.AddToRoleAsync(user, role), "Назначение роли");
        await tx.CommitAsync(ct);
        return user;
    }

    public async Task ChangeRoleAsync(string targetId, string role, string actorId,
        CancellationToken ct = default)
    {
        if (!IsAllowedRole(role)) throw new BusinessException("Неизвестная роль.", 400);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var target = await users.FindByIdAsync(targetId)
            ?? throw new BusinessException("Сотрудник не найден.", 404);
        if (string.Equals(target.Email, config["BootstrapAdmin:Email"], StringComparison.OrdinalIgnoreCase)
            && role != "Admin")
            throw new BusinessException("Основную учётную запись администратора нельзя понизить в роли.", 409);
        var currentRoles = await users.GetRolesAsync(target);
        if (currentRoles.Count == 1 && currentRoles[0] == role) return;
        if (targetId == actorId) throw new BusinessException("Нельзя изменять собственную роль.", 409);
        if (currentRoles.Contains("Admin") && role != "Admin")
        {
            var admins = await users.GetUsersInRoleAsync("Admin");
            if (admins.Count <= 1)
                throw new BusinessException("Нельзя убрать последнего администратора.", 409);
        }
        if (currentRoles.Count > 0)
            RequireSuccess(await users.RemoveFromRolesAsync(target, currentRoles), "Смена роли");
        RequireSuccess(await users.AddToRoleAsync(target, role), "Назначение роли");
        if (role != "Manager")
        {
            var assignments = await db.EventStaffAssignments.Where(x => x.UserId == targetId).ToListAsync(ct);
            db.EventStaffAssignments.RemoveRange(assignments);
            await db.SaveChangesAsync(ct);
        }
        // Invalidates previously issued cookies and JWTs carrying the former security stamp.
        RequireSuccess(await users.UpdateSecurityStampAsync(target), "Обновление безопасности");
        await tx.CommitAsync(ct);
    }

    public async Task AssignManagerAsync(Guid sessionId, string managerId, CancellationToken ct = default)
    {
        if (!await db.GameSessions.AnyAsync(x => x.Id == sessionId, ct))
            throw new BusinessException("Игра не найдена.", 404);
        var manager = await users.FindByIdAsync(managerId)
            ?? throw new BusinessException("Сотрудник не найден.", 404);
        if (!await users.IsInRoleAsync(manager, "Manager"))
            throw new BusinessException("Назначить на игру можно только менеджера.", 409);
        if (await db.EventStaffAssignments.AnyAsync(x => x.GameSessionId == sessionId && x.UserId == managerId, ct))
            return;
        db.EventStaffAssignments.Add(new EventStaffAssignment { GameSessionId = sessionId, UserId = managerId });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveManagerAsync(Guid sessionId, string managerId, CancellationToken ct = default)
    {
        var assignment = await db.EventStaffAssignments.FindAsync(new object[] { managerId, sessionId }, ct);
        if (assignment is null) return;
        db.EventStaffAssignments.Remove(assignment);
        await db.SaveChangesAsync(ct);
    }

    private static void RequireSuccess(IdentityResult result, string operation)
    {
        if (result.Succeeded) return;
        var details = string.Join("; ", result.Errors.Select(x => x.Description));
        throw new BusinessException($"{operation}: {details}", 400);
    }
}
