using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using MuzLoto.Web.Services.Messaging;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>The agreed «мы подтвердили вашу бронь» wording, plus the line that announces the receipt copy.</summary>
public sealed class ConfirmationMessageTests
{
    private static readonly Booking Booking = new()
    {
        TeamName = "МЯУ", Quantity = 2, TicketPriceSnapshot = 700m, ReceiptKey = "receipts/abc.png"
    };

    private static readonly GameSession Session = new()
    {
        Game = new Game { Title = "Квиз Сумерки" },
        StartsAt = new DateTimeOffset(2031, 11, 5, 10, 0, 0, TimeSpan.Zero), TimeZoneId = ScheduleTime.DefaultTimeZone,
        Venue = "Киса Бар, Светланская 21", DurationMinutes = 120, AgeLimit = 18
    };

    [Fact]
    public void WithAReceipt_TheTextAnnouncesTheCopyThatFollows()
    {
        // A messenger cannot send text and a file together: the receipt arrives as its own message.
        var text = ConfirmationMessage.Build(Booking, Session, Session.Game, receiptFollows: true);
        Assert.StartsWith("Здравствуйте, мы подтвердили вашу бронь!", text);
        Assert.EndsWith("Также дублируем ваш чек.", text);
        Assert.Contains("Итого: 1\u00A0400 ₽", text);
    }

    [Fact]
    public void WithoutAReceipt_NoEmptyPromise()
    {
        var text = ConfirmationMessage.Build(Booking, Session, Session.Game);
        Assert.DoesNotContain("Также дублируем ваш чек", text);
        Assert.EndsWith("Итого: 1\u00A0400 ₽", text);
    }
}
