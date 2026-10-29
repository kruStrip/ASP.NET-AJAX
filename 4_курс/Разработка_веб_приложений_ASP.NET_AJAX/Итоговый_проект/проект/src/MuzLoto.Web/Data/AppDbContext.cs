using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Game> Games => Set<Game>();
    public DbSet<GameSession> GameSessions => Set<GameSession>();
    public DbSet<GameImage> GameImages => Set<GameImage>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<BookingStatusHistory> BookingStatusHistory => Set<BookingStatusHistory>();
    public DbSet<EventStaffAssignment> EventStaffAssignments => Set<EventStaffAssignment>();
    public DbSet<ExternalSubmission> ExternalSubmissions => Set<ExternalSubmission>();
    public DbSet<QuantityChange> QuantityChanges => Set<QuantityChange>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<Game>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Title); // search by game title
        });
        b.Entity<GameSession>(e =>
        {
            e.HasOne(x => x.Game).WithMany(g => g.Sessions).HasForeignKey(x => x.GameId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Venue).HasMaxLength(250).IsRequired();
            e.Property(x => x.TicketPrice).HasPrecision(12, 2);
            e.HasIndex(x => x.StartsAt);
        });
        b.Entity<GameImage>(e =>
        {
            e.HasOne(x => x.GameSession).WithMany(s => s.Images).HasForeignKey(x => x.GameSessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
        });
        b.Entity<Booking>(e =>
        {
            e.HasOne(x => x.GameSession).WithMany(s => s.Bookings).HasForeignKey(x => x.GameSessionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Payment).WithOne(p => p.Booking).HasForeignKey<Payment>(p => p.BookingId).OnDelete(DeleteBehavior.Restrict);
            // Deleting an account must not remove paid history: the booking just loses its owner.
            e.HasOne(x => x.Owner).WithMany(u => u.OwnedBookings).HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.OwnerUserId);
            e.Property(x => x.TeamName).HasMaxLength(150).IsRequired();
            e.Property(x => x.CustomerName).HasMaxLength(150).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(40).IsRequired();
            e.Property(x => x.Telegram).HasMaxLength(100);
            e.Property(x => x.TicketPriceSnapshot).HasPrecision(12, 2);
            e.HasIndex(x => new { x.GameSessionId, x.Status });
            e.HasIndex(x => x.CreatedAt);
        });
        b.Entity<Payment>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.HasOne(x => x.ConfirmedByUser).WithMany().HasForeignKey(x => x.ConfirmedByUserId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<Refund>(e =>
        {
            e.HasOne(x => x.Payment).WithMany(p => p.Refunds)
                .HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.Property(x => x.TransferReference).HasMaxLength(200);
            e.HasIndex(x => x.Status);
            // At most one open or completed *cancellation* refund per payment; partial refunds for
            // reduced orders may be several. Cancelled requests stay in the audit trail.
            e.HasIndex(x => x.PaymentId).IsUnique()
                .HasFilter("\"Status\" IN (0, 1) AND \"Kind\" = 0")
                .HasDatabaseName("IX_Refunds_PaymentId_OpenCancellation");
            e.HasIndex(x => new { x.PaymentId, x.Kind });
        });
        b.Entity<BookingStatusHistory>(e =>
        {
            e.HasOne(x => x.Booking).WithMany(bk => bk.StatusHistory).HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<QuantityChange>(e =>
        {
            e.HasOne(x => x.Booking).WithMany(bk => bk.QuantityChanges).HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Refund).WithMany().HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Amount).HasPrecision(12, 2);
            e.Ignore(x => x.IsIncrease);
            // One surcharge waiting at a time per order.
            e.HasIndex(x => x.BookingId).IsUnique().HasFilter("\"Status\" = 0")
                .HasDatabaseName("IX_QuantityChanges_BookingId_Awaiting");
        });
        b.Entity<EventStaffAssignment>(e =>
        {
            e.HasKey(x => new { x.UserId, x.GameSessionId });
            e.HasOne(x => x.User).WithMany(u => u.EventAssignments).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.GameSession).WithMany(s => s.Staff).HasForeignKey(x => x.GameSessionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ExternalSubmission>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.ExternalSubmissionId).HasMaxLength(250).IsRequired();
            e.HasIndex(x => new { x.Provider, x.ExternalSubmissionId }).IsUnique();
            e.HasOne(x => x.Booking).WithMany(bk => bk.ExternalSubmissions).HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
