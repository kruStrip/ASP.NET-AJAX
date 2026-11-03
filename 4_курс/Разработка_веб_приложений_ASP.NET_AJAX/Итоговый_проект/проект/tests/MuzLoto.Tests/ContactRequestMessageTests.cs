using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using MuzLoto.Web.Services.Messaging;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>
/// The text a manager forwards to the customer when the messenger could not deliver the confirmation, and the same
/// text behind the «Скопировать текст» button in the booking card.
/// </summary>
public sealed class ContactRequestMessageTests
{
    private static readonly Booking Booking = new()
    {
        TeamName = "МЯУ", CustomerName = "Вика", Phone = "+79934902720", Telegram = "@team_msg",
        Quantity = 2, TicketPriceSnapshot = 700m
    };

    private static readonly GameSession Session = new()
    {
        Game = new Game { Title = "Квиз Сумерки" },
        StartsAt = new DateTimeOffset(2031, 11, 5, 10, 0, 0, TimeSpan.Zero), TimeZoneId = ScheduleTime.DefaultTimeZone,
        Venue = "Киса Бар, Светланская 21", DurationMinutes = 120, AgeLimit = 18
    };

    [Fact]
    public void CustomerText_StartsWithTheGreetingAndEndsWithTheTotal()
    {
        var text = ContactRequestMessage.CustomerText(Booking, Session, Session.Game);
        Assert.StartsWith("Здравствуйте!\nВы записаны на игру: Квиз Сумерки", text);
        Assert.EndsWith("Итого: 1\u00A0400 ₽", text);
        foreach (var line in new[] { "Дата: 05.11.2031", "Время: 20:00", "Локация: Киса Бар, Светланская 21",
                     "Продолжительность: 2 часа", "Возрастное ограничение: 18+", "Имя: Вика", "Команда: МЯУ",
                     "Телефон: 79934902720", "Кол-во билетов: 2" })
            Assert.Contains(line, text);
        Assert.DoesNotContain("Связаться с клиентом", text);   // that header is for the managers only
    }

    [Fact]
    public void Build_KeepsTheYouCanCopyTextAtTheEnd()
    {
        var text = ContactRequestMessage.Build(Booking, Session, Session.Game);
        Assert.StartsWith("Связаться с клиентом", text);
        Assert.Contains("Telegram: https://t.me/team_msg", text);
        Assert.Contains("WhatsApp: https://wa.me/79934902720", text);
        Assert.Contains("__________________________", text);
        Assert.EndsWith(ContactRequestMessage.CustomerText(Booking, Session, Session.Game), text);
    }
}
