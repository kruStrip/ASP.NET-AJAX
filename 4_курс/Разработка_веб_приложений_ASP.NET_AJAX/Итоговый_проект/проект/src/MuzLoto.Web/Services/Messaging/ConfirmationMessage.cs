using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>The text the customer gets when the payment is confirmed (the wording agreed with the customer).</summary>
public static class ConfirmationMessage
{
    public static string Build(Booking booking, GameSession session, Game game)
    {
        var local = ScheduleTime.ToLocal(session.StartsAt, session.TimeZoneId);
        var total = booking.TicketPriceSnapshot * booking.Quantity;
        return $"""
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
    }
}
