using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>The text the customer gets when the payment is confirmed (the wording agreed with the customer).</summary>
public static class ConfirmationMessage
{
    /// <param name="receiptFollows">
    /// The customer's own receipt is queued right after this text. A messenger cannot send text and a file together,
    /// so the file arrives on its own — the text has to announce it (decided 2026-11-03).
    /// </param>
    public static string Build(Booking booking, GameSession session, Game game, bool receiptFollows = false)
    {
        var local = ScheduleTime.ToLocal(session.StartsAt, session.TimeZoneId);
        var total = booking.TicketPriceSnapshot * booking.Quantity;
        var text = $"""
            Здравствуйте, мы подтвердили вашу бронь!

            {game.Title}

            Дата: {local:dd.MM.yyyy}
            Время: {local:HH:mm}
            Локация: {session.Venue}
            Продолжительность: {PublicSchedule.Duration(session.DurationMinutes)}
            Возрастное ограничение: {session.AgeLimit}+

            Команда: {booking.TeamName}
            Кол-во билетов: {booking.Quantity}

            Итого: {total.ToString("#,0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} ₽
            """;
        return receiptFollows ? text + "\n\nТакже дублируем ваш чек." : text;
    }
}
