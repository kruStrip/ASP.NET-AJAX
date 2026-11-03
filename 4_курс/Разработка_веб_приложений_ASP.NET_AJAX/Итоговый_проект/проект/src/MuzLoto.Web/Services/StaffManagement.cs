using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Hubs;

namespace MuzLoto.Web.Services;

/// <summary>Administrative operations on staff and game assignments. Never accepts a caller-supplied role outside the allowlist.</summary>
public sealed class StaffManagement(AppDbContext db, UserManager<AppUser> users, IConfiguration config,
    HubAccessRevoker revoker)
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
        if (role == "Manager" && (await users.GetUsersInRoleAsync("Manager")).Count == 1)
        {
            // The first manager becomes the default for games that already exist without one.
            var unassigned = await db.GameSessions.Where(s => !s.Staff.Any()).Select(s => s.Id).ToListAsync(ct);
            db.EventStaffAssignments.AddRange(unassigned.Select(id =>
                new EventStaffAssignment { GameSessionId = id, UserId = user.Id }));
            await db.SaveChangesAsync(ct);
        }
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
        // Live SignalR connections were authorised under the old role: drop them from every game group now.
        await revoker.RevokeAsync(targetId);
    }

    /// <summary>Deletes an account: paid history stays, game assignments go away with the account.</summary>
    public async Task DeleteAsync(string targetId, string actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetId)) throw new BusinessException("Аккаунт не найден.", 404);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var target = await users.FindByIdAsync(targetId)
            ?? throw new BusinessException("Аккаунт не найден.", 404);
        if (targetId == actorId)
            throw new BusinessException("Нельзя удалить собственный аккаунт.", 409);
        if (string.Equals(target.Email, config["BootstrapAdmin:Email"], StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("Основную учётную запись администратора нельзя удалить.", 409);
        if (await users.IsInRoleAsync(target, "Admin"))
        {
            var admins = await users.GetUsersInRoleAsync("Admin");
            if (admins.Count <= 1)
                throw new BusinessException("Нельзя удалить последнего администратора.", 409);
        }
        // Bookings keep their history: OwnerUserId is set to null, only the account itself disappears.
        RequireSuccess(await users.DeleteAsync(target), "Удаление аккаунта");
        await tx.CommitAsync(ct);
        await revoker.RevokeAsync(targetId);
    }

    /// <summary>Ends every sign-in of the account: a new security stamp invalidates cookies and JWTs on all devices.</summary>
    public async Task EndSessionsAsync(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId)) throw new BusinessException("Аккаунт не найден.", 404);
        var target = await users.FindByIdAsync(targetId)
            ?? throw new BusinessException("Аккаунт не найден.", 404);
        RequireSuccess(await users.UpdateSecurityStampAsync(target), "Завершение сессий");
        await revoker.RevokeAsync(targetId);
    }

    /// <summary>The first Manager account in the system (earliest created) — the default assignee for new games.</summary>
    public async Task<AppUser?> FindDefaultManagerAsync(CancellationToken ct = default)
    {
        var managers = await users.GetUsersInRoleAsync("Manager");
        return managers
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.UserName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Gives a game without any manager its default assignee. Existing assignments are never touched.</summary>
    public async Task AssignDefaultManagerAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (await db.EventStaffAssignments.AnyAsync(x => x.GameSessionId == sessionId, ct)) return;
        if (await FindDefaultManagerAsync(ct) is not { } manager) return;
        db.EventStaffAssignments.Add(new EventStaffAssignment { GameSessionId = sessionId, UserId = manager.Id });
        await db.SaveChangesAsync(ct);
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
        await revoker.RevokeAsync(managerId, sessionId);
    }

    private static void RequireSuccess(IdentityResult result, string operation)
    {
        if (result.Succeeded) return;
        var details = string.Join("; ", result.Errors.Select(x => x.Description));
        throw new BusinessException($"{operation}: {details}", 400);
    }
}
