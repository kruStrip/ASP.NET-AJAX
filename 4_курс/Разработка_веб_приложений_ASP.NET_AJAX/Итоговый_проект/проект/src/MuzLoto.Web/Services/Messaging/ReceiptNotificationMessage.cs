using System.Globalization;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// What the managers get in the Telegram group topic «Подтвердить игру» when a customer uploads a payment
/// receipt: the customer's phone and name, the amount owed and a link to the confirmation card. The receipt
/// itself is attached to the same message by <see cref="TelegramBotChannel"/>.
/// </summary>
public static class ReceiptNotificationMessage
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Build(Booking booking, GameSession session, Game game, string siteUrl, int maxLength = 1024)
    {
        var local = ScheduleTime.ToLocal(session.StartsAt, session.TimeZoneId);
        var total = booking.TicketPriceSnapshot * booking.Quantity;
        // Telegram captions are limited (1024 characters): keep the essentials and always keep the link.
        var text = $"""
            Новая оплата ждёт подтверждения

            Номер клиента: {booking.Phone}
            Имя клиента: {booking.CustomerName}
            Сумма к оплате: {Money(total)} ₽

            Игра: {game.Title}
            Дата: {local:dd.MM.yyyy} {local:HH:mm}
            Команда: {booking.TeamName}
            Билетов: {booking.Quantity}

            Карточка подтверждения: {siteUrl.TrimEnd('/')}/bookings/pending#booking-{booking.Id:N}
            """;
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    private static string Money(decimal amount) => amount.ToString("#,0.##", Ru);
}
