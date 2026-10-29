using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

public static class BookingPolicies
{
    /// <summary>Change the booking's own details: its owner (while unpaid), an admin or a manager assigned to the game.</summary>
    public const string CanEditBooking = nameof(CanEditBooking);
    /// <summary>Staff operations (confirm payment, cancel, restore): an admin or a manager assigned to the game.</summary>
    public const string CanManageBooking = nameof(CanManageBooking);
}

public sealed class BookingRequirement(bool ownerAllowed) : IAuthorizationRequirement
{
    public bool OwnerAllowed { get; } = ownerAllowed;
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
        // Once paid, changes affect seats and money, so they stay with staff.
        if (requirement.OwnerAllowed && userId is not null && booking.OwnerUserId == userId &&
            booking.Status == BookingStatus.Pending)
            context.Succeed(requirement);
    }
}
