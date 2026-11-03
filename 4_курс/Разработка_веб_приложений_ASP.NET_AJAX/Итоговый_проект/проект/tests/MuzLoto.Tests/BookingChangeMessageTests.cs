using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using MuzLoto.Web.Services.Messaging;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>
/// The staff notices in the Telegram topics «Изменения в бронях» and «Отмена игр» (decision of 2026-11-04):
/// who changed what, which booking, and how to open it.
/// </summary>
public sealed class BookingChangeMessageTests
{
    private static readonly GameSession Session = new()
    {
        Game = new Game { Title = "Квиз Сумерки" },
        StartsAt = new DateTimeOffset(2031, 11, 5, 10, 0, 0, TimeSpan.Zero), TimeZoneId = ScheduleTime.DefaultTimeZone,
        Venue = "Киса Бар", DurationMinutes = 120, AgeLimit = 18
    };

    private static Booking Booking(BookingStatus status = BookingStatus.Pending) => new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        GameSession = Session, Status = status, TeamName = "МЯУ", CustomerName = "Вика",
        Phone = "+79934902720", Quantity = 2, TicketPriceSnapshot = 700m
    };

    [Fact]
    public void Changed_ShowsTheBookingTheActorAndWhatChanged()
    {
        var text = BookingChangeMessage.Changed(Booking(), Session, Session.Game, "Администратор",
            ["Билетов: 2 → 3"], "https://muzloto.example/");

        Assert.StartsWith("Изменение брони", text);
        foreach (var line in new[] { "Игра: Квиз Сумерки", "Дата: 05.11.2031 20:00", "Команда: МЯУ",
                     "Клиент: Вика", "Телефон: 79934902720", "Изменил: Администратор", "• Билетов: 2 → 3" })
            Assert.Contains(line, text);
        Assert.Contains("https://muzloto.example/bookings/pending#booking-11111111222233334444555555555555", text);
    }

    [Fact]
    public void Cancelled_ShowsThePaymentAndRefundState()
    {
        var text = BookingChangeMessage.Cancelled(Booking(BookingStatus.Cancelled), Session, Session.Game,
            "Менеджер", "оплачено 1\u00A0400 ₽", "возврат 1\u00A0400 ₽ поставлен в очередь", "https://muzloto.example");

        Assert.StartsWith("Отмена брони", text);
        Assert.Contains("Оплата: оплачено 1\u00A0400 ₽", text);
        Assert.Contains("Возврат: возврат 1\u00A0400 ₽ поставлен в очередь", text);
        Assert.Contains("Отменил: Менеджер", text);
        Assert.Contains("https://muzloto.example/bookings#booking-11111111222233334444555555555555", text);
    }

    [Fact]
    public void Changed_WithoutAnActor_SimplyOmitsTheLine()
    {
        var text = BookingChangeMessage.Changed(Booking(), Session, Session.Game, null, ["Билетов: 2 → 3"], "https://muzloto.example");
        Assert.DoesNotContain("Изменил:", text);
    }

    [Fact]
    public void Messages_StayWithinTheLengthTelegramAccepts()
    {
        var longChange = string.Join(" → ", Enumerable.Repeat(new string('Я', 500), 4));
        var text = BookingChangeMessage.Changed(Booking(), Session, Session.Game, "Администратор", [longChange], "https://muzloto.example");
        Assert.True(text.Length <= 1024, $"the message is {text.Length} characters");
        Assert.EndsWith("…", text);
    }
}
