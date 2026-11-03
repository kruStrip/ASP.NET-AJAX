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

    /// <summary>Telegram shows a «Copy» button for a &lt;pre&gt; block — that is the point of the HTML version.</summary>
    [Fact]
    public void TelegramHtml_WrapsOnlyTheCustomerTextIntoTheCopyableBlock()
    {
        var html = ContactRequestMessage.ToTelegramHtml(ContactRequestMessage.Build(Booking, Session, Session.Game));
        Assert.Contains("<pre>" + ContactRequestMessage.CustomerText(Booking, Session, Session.Game).TrimEnd() + "</pre>", html);
        Assert.True(html.IndexOf("https://t.me/team_msg", StringComparison.Ordinal) < html.IndexOf("<pre>", StringComparison.Ordinal));
        Assert.EndsWith("</pre>", html);
        Assert.Equal(1, html.Split("<pre>").Length - 1);
    }

    [Fact]
    public void TelegramHtml_EscapesWhatTheCustomerCouldHaveTyped()
    {
        var booking = new Booking
        {
            TeamName = "<b>МЯУ</b>", CustomerName = "Вика & Ко", Phone = "+79934902720",
            Quantity = 2, TicketPriceSnapshot = 700m
        };
        var session = new GameSession
        {
            Game = new Game { Title = "Квиз <Сумерки>" },
            StartsAt = Session.StartsAt, TimeZoneId = Session.TimeZoneId, Venue = "Киса & Бар",
            DurationMinutes = 120, AgeLimit = 18
        };
        var html = ContactRequestMessage.ToTelegramHtml(ContactRequestMessage.Build(booking, session, session.Game));
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;b&gt;МЯУ&lt;/b&gt;", html);
        Assert.Contains("Вика &amp; Ко", html);
        Assert.Contains("Квиз &lt;Сумерки&gt;", html);
        Assert.Contains("Киса &amp; Бар", html);
    }

    /// <summary>
    /// Telegram trims captions at 1024 characters and would reject a broken tag: the block is cut before the limit,
    /// the closing tag is always there.
    /// </summary>
    [Fact]
    public void TelegramHtml_StaysWithinTheCaptionLimit()
    {
        var booking = new Booking
        {
            TeamName = new string('Я', 900), CustomerName = new string('А', 900), Phone = "+79934902720",
            Quantity = 2, TicketPriceSnapshot = 700m
        };
        var html = ContactRequestMessage.ToTelegramHtml(ContactRequestMessage.Build(booking, Session, Session.Game));
        Assert.True(html.Length <= 1024, $"the caption is {html.Length} characters");
        Assert.EndsWith("</pre>", html);
        Assert.Contains("…</pre>", html);
        Assert.Equal(1, html.Split("<pre>").Length - 1);
        Assert.Equal(1, html.Split("</pre>").Length - 1);
    }
}
