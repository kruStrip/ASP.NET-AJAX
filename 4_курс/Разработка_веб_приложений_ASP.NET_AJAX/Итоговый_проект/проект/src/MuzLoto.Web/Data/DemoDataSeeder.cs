using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Data;

/// <summary>
/// Fictional demo data for local development and the course defence: 3–5 rows in every business table.
/// Runs only in Development, only when Seed:DemoData is true and only on an empty Games table, so restarts never duplicate it.
/// </summary>
public static class DemoDataSeeder
{
    public const string ManagerPassword = "Manager2026!";
    public const string UserPassword = "User2026!";

    public static async Task SeedAsync(IServiceProvider services, IConfiguration config, IHostEnvironment env)
    {
        if (!env.IsDevelopment() || !config.GetValue("Seed:DemoData", false)) return;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Games.AnyAsync()) return;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var manager1 = await EnsureUserAsync(users, "manager1@muzloto.local", "Мария Менеджер", "Manager", ManagerPassword);
        var manager2 = await EnsureUserAsync(users, "manager2@muzloto.local", "Михаил Менеджер", "Manager", ManagerPassword);
        var player = await EnsureUserAsync(users, "user@muzloto.local", "Ульяна Игрок", "User", UserPassword);
        var admin = (await users.GetUsersInRoleAsync("Admin")).FirstOrDefault();

        var games = new[]
        {
            new Game { Title = "Хиты 2000-х", Description1 = "Угадывайте песни, которые звучали из каждого утюга.",
                Description2 = "Пять раундов: от интро до караоке-финала.", Description3 = "Команды до 8 человек." },
            new Game { Title = "Русский рок", Description1 = "Кино, Сплин, Земфира и не только.",
                Description2 = "Раунд «Угадай по аккордам» для самых внимательных." },
            new Game { Title = "Саундтреки", Description1 = "Музыка из фильмов, сериалов и мультфильмов.",
                Description2 = "Угадайте фильм по первым нотам.", Description3 = "Призы лучшим командам." }
        };
        db.Games.AddRange(games);

        var start = DateTime.UtcNow.Date.AddHours(9); // 19:00 in Vladivostok
        GameSession Session(Game game, int days, int capacity) => new()
        {
            Game = game, StartsAt = new DateTimeOffset(start.AddDays(days), TimeSpan.Zero),
            TimeZoneId = "Asia/Vladivostok", Venue = "Демо-бар «Пластинка», ул. Примерная, 1",
            MapUrl = "https://2gis.ru/vladivostok", DurationMinutes = 150, AgeLimit = 18,
            TicketPrice = 700m, Capacity = capacity
        };
        var sessions = new[]
        {
            Session(games[0], 3, 40), Session(games[1], 7, 30), Session(games[2], 10, 26),
            Session(games[0], 14, 50), Session(games[1], 21, 30)
        };
        db.GameSessions.AddRange(sessions);
        db.GameImages.AddRange(
            new GameImage { GameSession = sessions[0], StorageKey = "/img/covers/1.svg" },
            new GameImage { GameSession = sessions[1], StorageKey = "/img/covers/2.svg" },
            new GameImage { GameSession = sessions[2], StorageKey = "/img/covers/3.svg" },
            new GameImage { GameSession = sessions[3], StorageKey = "/img/covers/1.svg" });
        db.EventStaffAssignments.AddRange(
            new EventStaffAssignment { UserId = manager1.Id, GameSession = sessions[0] },
            new EventStaffAssignment { UserId = manager1.Id, GameSession = sessions[1] },
            new EventStaffAssignment { UserId = manager2.Id, GameSession = sessions[2] });

        var now = DateTimeOffset.UtcNow;
        var n = 0;
        Booking Book(GameSession s, string team, int qty, string? owner = null)
        {
            n++;
            var b = new Booking
            {
                GameSession = s, TeamName = team, CustomerName = $"Демо-клиент {n}",
                Phone = $"+7900000000{n % 10}", Telegram = $"@demo_player{n}", Quantity = qty,
                TicketPriceSnapshot = s.TicketPrice, OwnerUserId = owner, CreatedAt = now.AddDays(-2).AddMinutes(n)
            };
            db.Bookings.Add(b);
            db.ExternalSubmissions.Add(new ExternalSubmission { Provider = "FormDesigner", ExternalSubmissionId = $"fd-demo-{n}", Booking = b });
            return b;
        }
        void Confirm(Booking b)
        {
            b.Status = BookingStatus.Confirmed;
            b.ConfirmedAt = now.AddDays(-1);
            b.Payment = new Payment { Amount = b.Quantity * b.TicketPriceSnapshot, ConfirmedByUserId = admin?.Id, ConfirmedAt = now.AddDays(-1) };
            db.BookingStatusHistory.Add(new BookingStatusHistory { Booking = b, OldStatus = BookingStatus.Pending,
                NewStatus = BookingStatus.Confirmed, ChangedByUserId = admin?.Id, ChangedAt = now.AddDays(-1), Reason = "Демо: оплата подтверждена" });
        }
        void CancelPaid(Booking b, RefundStatus refundStatus)
        {
            Confirm(b);
            b.Status = BookingStatus.Cancelled;
            b.CancelledAt = now.AddDays(-1).AddHours(2);
            db.BookingStatusHistory.Add(new BookingStatusHistory { Booking = b, OldStatus = BookingStatus.Confirmed,
                NewStatus = BookingStatus.Cancelled, ChangedByUserId = admin?.Id, ChangedAt = b.CancelledAt.Value, Reason = "Демо: отмена" });
            db.Refunds.Add(new Refund { Payment = b.Payment!, Amount = b.Payment!.Amount, Status = refundStatus,
                RequestedAt = b.CancelledAt.Value, RequestedByUserId = admin?.Id,
                CompletedAt = refundStatus == RefundStatus.Completed ? now.AddHours(-3) : null,
                CompletedByUserId = refundStatus == RefundStatus.Completed ? admin?.Id : null });
        }

        Book(sessions[0], "Меломаны", 4);
        Book(sessions[1], "Громкие соседи", 6, player.Id);
        Book(sessions[2], "Тихий час", 3);
        var stars = Book(sessions[0], "Звёзды караоке", 5);
        Confirm(stars);
        var drums = Book(sessions[1], "Басы и барабаны", 8);
        Confirm(drums);
        var playback = Book(sessions[2], "Фонограмма", 4, player.Id);
        Confirm(playback);

        // Ticket-count changes on paid orders: one reduced (partial refund waiting), one waiting for a surcharge,
        // one surcharge that was dropped.
        var partialRefund = new Refund { Payment = drums.Payment!, Amount = 2 * drums.TicketPriceSnapshot,
            Kind = RefundKind.QuantityDecrease, RequestedAt = now.AddHours(-5), RequestedByUserId = admin?.Id };
        db.Refunds.Add(partialRefund);
        db.QuantityChanges.AddRange(
            new QuantityChange { Booking = drums, OldQuantity = 8, NewQuantity = 6, Amount = partialRefund.Amount,
                Status = QuantityChangeStatus.Applied, RequestedAt = now.AddHours(-5), RequestedByUserId = admin?.Id,
                CompletedAt = now.AddHours(-5), CompletedByUserId = admin?.Id, Refund = partialRefund },
            new QuantityChange { Booking = stars, OldQuantity = 5, NewQuantity = 7, Amount = 2 * stars.TicketPriceSnapshot,
                Status = QuantityChangeStatus.AwaitingPayment, RequestedAt = now.AddHours(-2), RequestedByUserId = admin?.Id },
            new QuantityChange { Booking = playback, OldQuantity = 4, NewQuantity = 5, Amount = playback.TicketPriceSnapshot,
                Status = QuantityChangeStatus.Cancelled, RequestedAt = now.AddHours(-3), RequestedByUserId = admin?.Id,
                CompletedAt = now.AddHours(-1), CompletedByUserId = admin?.Id });
        drums.Quantity = 6;
        CancelPaid(Book(sessions[0], "Опоздавшие", 2), RefundStatus.Pending);
        CancelPaid(Book(sessions[3], "Передумавшие", 3), RefundStatus.Completed);
        CancelPaid(Book(sessions[4], "Вернёмся позже", 2), RefundStatus.Pending);
        await db.SaveChangesAsync();
    }

    private static async Task<AppUser> EnsureUserAsync(UserManager<AppUser> users, string email, string name, string role, string password)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new AppUser { UserName = email, Email = email, FullName = name, EmailConfirmed = true };
            Ensure(await users.CreateAsync(user, password));
        }
        if (!await users.IsInRoleAsync(user, role)) Ensure(await users.AddToRoleAsync(user, role));
        return user;
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Demo seed failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
