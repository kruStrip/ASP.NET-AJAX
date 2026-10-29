using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

public static class BookingPolicies
{
    /// <summary>Read the booking: its owner (any status), an admin or a manager assigned to the game.</summary>
    public const string CanViewBooking = nameof(CanViewBooking);
    /// <summary>Change the booking's own details: its owner (while unpaid), an admin or a manager assigned to the game.</summary>
    public const string CanEditBooking = nameof(CanEditBooking);
    /// <summary>Staff operations (confirm payment, cancel, restore): an admin or a manager assigned to the game.</summary>
    public const string CanManageBooking = nameof(CanManageBooking);
}

public enum OwnerAccess { None, PendingOnly, AnyStatus }

public sealed class BookingRequirement(OwnerAccess owner) : IAuthorizationRequirement
{
    public OwnerAccess Owner { get; } = owner;
}

/// <summary>Resource-based check: the booking is loaded from the database by the caller and compared with the current user.</summary>
public sealed class BookingAuthorizationHandler(StaffAccess staff) : AuthorizationHandler<BookingRequirement, Booking>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, BookingRequirement requirement, Booking booking)
    {
        if (await staff.CanManageAsync(context.User, booking.GameSessionId))
        {
            context.Succeed(requirement);
            return;
        }
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null || booking.OwnerUserId != userId) return;
        // Once paid, changes affect seats and money, so they stay with staff.
        if (requirement.Owner == OwnerAccess.AnyStatus ||
            (requirement.Owner == OwnerAccess.PendingOnly && booking.Status == BookingStatus.Pending))
            context.Succeed(requirement);
    }
}
