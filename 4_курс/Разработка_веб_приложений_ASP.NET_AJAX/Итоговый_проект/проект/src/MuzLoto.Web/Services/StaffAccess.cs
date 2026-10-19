using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;

namespace MuzLoto.Web.Services;

public sealed class StaffAccess(AppDbContext db)
{
    public async Task<bool> CanManageAsync(ClaimsPrincipal user, Guid sessionId, CancellationToken ct = default)
    {
        if (user.IsInRole("Admin")) return true;
        if (!user.IsInRole("Manager")) return false;
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId is not null && await db.EventStaffAssignments.AnyAsync(
            x => x.UserId == userId && x.GameSessionId == sessionId, ct);
    }
}
