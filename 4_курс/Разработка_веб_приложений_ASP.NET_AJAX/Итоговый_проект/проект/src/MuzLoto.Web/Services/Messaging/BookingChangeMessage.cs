using System.Globalization;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// Manager notifications in the Telegram group: «Изменения в бронях» when staff edited a booking (name, team,
/// Telegram, ticket count with a refund or a surcharge, restoring a cancelled booking) and «Отмена игр» when staff
/// cancelled a booking (decision of 2026-11-04). They are plain group messages — no receipt is attached — with the
/// game, the customer, who did it and what changed, plus a link to the booking card.
/// </summary>
public static class BookingChangeMessage
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Changed(Booking booking, GameSession session, Game game, string? actor,
        IReadOnlyList<string> changes, string siteUrl, int maxLength = 1024)
    {
        var lines = Header("Изменение брони", booking, session, game);
        if (actor is { Length: > 0 }) lines.Add($"Изменил: {actor}");
        lines.Add("");
        lines.Add("Что изменилось:");
        foreach (var change in changes) lines.Add("• " + change);
        lines.Add("");
        lines.Add($"Карточка: {BookingUrl(booking, siteUrl)}");
        return Fit(lines, maxLength);
    }

    /// <param name="payment">What happened with the money, e.g. «оплачено 2 800 ₽» or «не оплачено».</param>
    /// <param name="refund">The refund state, e.g. «возврат 2 800 ₽ отмечен как выполненный» or «возврат не требуется».</param>
    public static string Cancelled(Booking booking, GameSession session, Game game, string? actor,
        string payment, string refund, string siteUrl, int maxLength = 1024)
    {
        var lines = Header("Отмена брони", booking, session, game);
        lines.Add($"Оплата: {payment}");
        lines.Add($"Возврат: {refund}");
        if (actor is { Length: > 0 }) lines.Add($"Отменил: {actor}");
        lines.Add("");
        lines.Add($"Карточка: {BookingUrl(booking, siteUrl)}");
        return Fit(lines, maxLength);
    }

    /// <summary>How much the customer paid for the whole order, in words a manager reads in the group.</summary>
    public static string PaymentState(Booking booking) =>
        $"оплачено {Money(booking.TicketPriceSnapshot * booking.Quantity)} ₽";

    private static List<string> Header(string title, Booking booking, GameSession session, Game game)
    {
        var local = ScheduleTime.ToLocal(session.StartsAt, session.TimeZoneId);
        return
        [
            title,
            "",
            $"Игра: {game.Title}",
            $"Дата: {local:dd.MM.yyyy} {local:HH:mm}",
            $"Команда: {booking.TeamName}",
            $"Клиент: {booking.CustomerName}",
            $"Телефон: {TelegramHtml.Digits(booking.Phone)}",
            $"Билетов: {booking.Quantity}"
        ];
    }

    /// <summary>The pending card has anchors; a confirmed booking is looked up on «Покупки билетов».</summary>
    private static string BookingUrl(Booking booking, string siteUrl)
    {
        var baseUrl = siteUrl.TrimEnd('/');
        return booking.Status == BookingStatus.Pending
            ? $"{baseUrl}/bookings/pending#booking-{booking.Id:N}"
            : $"{baseUrl}/bookings#booking-{booking.Id:N}";
    }

    /// <summary>Telegram rejects messages over 4096 characters; a long change list keeps its start.</summary>
    private static string Fit(List<string> lines, int maxLength)
    {
        var text = string.Join('\n', lines);
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    /// <summary>«2 800» without the currency sign, so callers glue «₽» themselves.</summary>
    public static string Money(decimal amount) => amount.ToString("#,0.##", Ru);
}
