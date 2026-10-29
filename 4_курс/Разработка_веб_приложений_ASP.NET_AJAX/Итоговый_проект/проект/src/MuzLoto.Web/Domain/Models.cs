using Microsoft.AspNetCore.Identity;

namespace MuzLoto.Web.Domain;

public class AppUser : IdentityUser
{
    public string? FullName { get; set; }
    public ICollection<EventStaffAssignment> EventAssignments { get; set; } = new List<EventStaffAssignment>();
    public ICollection<Booking> OwnedBookings { get; set; } = new List<Booking>();
}

public class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Description1 { get; set; }
    public string? Description2 { get; set; }
    public string? Description3 { get; set; }
    public string? Description4 { get; set; }
    public string? Description5 { get; set; }
    public ICollection<GameSession> Sessions { get; set; } = new List<GameSession>();
}

public class GameSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public Game Game { get; set; } = null!;
    public DateTimeOffset StartsAt { get; set; }
    public string TimeZoneId { get; set; } = "Etc/UTC";
    public string Venue { get; set; } = string.Empty;
    public string? MapUrl { get; set; }
    public int DurationMinutes { get; set; } = 120;
    public int AgeLimit { get; set; } = 12;
    public decimal TicketPrice { get; set; }
    public int Capacity { get; set; }
    public bool SalesOpen { get; set; } = true;
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Hidden from operational lists; used instead of deleting a session that has purchases.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<GameImage> Images { get; set; } = new List<GameImage>();
    public ICollection<EventStaffAssignment> Staff { get; set; } = new List<EventStaffAssignment>();
}

public class GameImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameSessionId { get; set; }
    public GameSession GameSession { get; set; } = null!;
    public string StorageKey { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int Version { get; set; } = 1;
}

public enum BookingStatus { Pending, Confirmed, Cancelled }
public class Booking
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? LegacyId { get; set; }
    public Guid GameSessionId { get; set; }
    public GameSession GameSession { get; set; } = null!;
    /// <summary>Registered account that created the booking; null for anonymous/external submissions.</summary>
    public string? OwnerUserId { get; set; }
    public AppUser? Owner { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string? Telegram { get; set; }
    public int Quantity { get; set; }
    public decimal TicketPriceSnapshot { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public Payment? Payment { get; set; }
    public ICollection<BookingStatusHistory> StatusHistory { get; set; } = new List<BookingStatusHistory>();
    public ICollection<ExternalSubmission> ExternalSubmissions { get; set; } = new List<ExternalSubmission>();
}

public enum PaymentStatus { Confirmed }
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public Booking Booking { get; set; } = null!;
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Confirmed;
    public string? ConfirmedByUserId { get; set; }
    public AppUser? ConfirmedByUser { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<Refund> Refunds { get; set; } = new List<Refund>();
}

/// <summary>Only Completed refunds represent money actually returned to the customer.</summary>
public enum RefundStatus { Pending, Completed, Cancelled }

public class Refund
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;
    public decimal Amount { get; set; }
    public RefundStatus Status { get; set; } = RefundStatus.Pending;
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? RequestedByUserId { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedByUserId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelledByUserId { get; set; }
    public string? TransferReference { get; set; }
}

public class BookingStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public Booking Booking { get; set; } = null!;
    public BookingStatus OldStatus { get; set; }
    public BookingStatus NewStatus { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ChangedByUserId { get; set; }
    public string? Reason { get; set; }
}

public class EventStaffAssignment
{
    public string UserId { get; set; } = string.Empty;
    public AppUser User { get; set; } = null!;
    public Guid GameSessionId { get; set; }
    public GameSession GameSession { get; set; } = null!;
}

public class ExternalSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = "FormDesigner";
    public string ExternalSubmissionId { get; set; } = string.Empty;
    public Guid? BookingId { get; set; }
    public Booking? Booking { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
