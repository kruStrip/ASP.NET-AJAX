using System.Globalization;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// What the managers get in the Telegram group topic «Связаться с клиентом» when the customer's confirmation could
/// not be delivered: links to the customer, the phone, and the whole text in one piece, so a manager can forward it
/// by hand. The customer's receipt (if uploaded) is attached to the same message by <see cref="TelegramBotChannel"/>.
/// </summary>
public static class ContactRequestMessage
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Build(Booking booking, GameSession session, Game game, int maxLength = 1024)
    {
        var phone = Digits(booking.Phone);
        var nick = booking.Telegram?.Trim().TrimStart('@');
        var links = new List<string>();
        if (nick is { Length: > 0 }) links.Add($"Telegram: https://t.me/{nick}");
        links.Add($"WhatsApp: https://wa.me/{phone}");
        links.Add($"Телефон: {phone}");
        var text = $"""
            Связаться с клиентом

            {string.Join('\n', links)}

            Для отправки информации клиенту:
            __________________________
            {CustomerText(booking, session, game)}
            """;
        // Telegram captions are limited (1024 characters): the text is what the manager forwards, so keep the start.
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// The message itself, without the managers' header: what a manager pastes into the customer's chat. The same
    /// text is copied by the «Скопировать текст» button in the booking card, so the wording never drifts.
    /// </summary>
    public static string CustomerText(Booking booking, GameSession session, Game game)
    {
        var local = ScheduleTime.ToLocal(session.StartsAt, session.TimeZoneId);
        var total = booking.TicketPriceSnapshot * booking.Quantity;
        return $"""
            Здравствуйте!
            Вы записаны на игру: {game.Title}

            Дата: {local:dd.MM.yyyy}
            Время: {local:HH:mm}
            Локация: {session.Venue}
            Продолжительность: {PublicSchedule.Duration(session.DurationMinutes)}
            Возрастное ограничение: {session.AgeLimit}+

            Имя: {booking.CustomerName}
            Команда: {booking.TeamName}
            Телефон: {Digits(booking.Phone)}
            Кол-во билетов: {booking.Quantity}

            Итого: {Money(total)} ₽
            """;
    }

    private static string Money(decimal amount) => amount.ToString("#,0.##", Ru);

    /// <summary>Wazzup wants digits only: +7 914 000-00-00 → 79140000000.</summary>
    private static string Digits(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length == 11 && digits[0] == '8' ? '7' + digits[1..] : digits;
    }
}
