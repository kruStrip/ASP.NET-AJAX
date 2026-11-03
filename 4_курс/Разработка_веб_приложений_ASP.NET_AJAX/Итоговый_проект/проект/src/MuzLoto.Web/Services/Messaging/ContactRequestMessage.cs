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

    /// <summary>The line between the managers' header and the text the manager forwards to the customer.</summary>
    private const string Separator = "__________________________";

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
            {Separator}
            {CustomerText(booking, session, game)}
            """;
        // Telegram captions are limited (1024 characters): the text is what the manager forwards, so keep the start.
        return text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// The same text as HTML for the Telegram bot (parse_mode=HTML): the customer's block sits in a &lt;pre&gt; element,
    /// and Telegram shows it as a monospace block with a «Copy» button — one tap instead of selecting a long message
    /// on a phone (decision of 2026-11-03). Everything is HTML-escaped: names and venues may contain &lt;, &gt; or &amp;.
    /// The whole message must still fit Telegram's 1024-character caption limit, so the customer's block is shortened
    /// (with «…») when the escaping makes it too long.
    /// </summary>
    public static string ToTelegramHtml(string text, int maxLength = 1024)
    {
        var at = text.IndexOf(Separator, StringComparison.Ordinal);
        // Without the separator (a message queued before this change) the whole text becomes the copyable block.
        var header = at < 0 ? "" : text[..(at + Separator.Length)];
        var body = (at < 0 ? text : text[(at + Separator.Length)..]).Trim('\n');
        var prefix = (header.Length > 0 ? Escape(header) + "\n" : "") + "<pre>";
        const string suffix = "</pre>";
        var budget = maxLength - prefix.Length - suffix.Length;
        if (Escape(body).Length > budget)
        {
            // The ellipsis itself takes a character, so it is counted while shrinking the block.
            do { body = (body.Length > 0 ? body[..^1] : body).TrimEnd(); }
            while (body.Length > 0 && Escape(body + "…").Length > budget);
            body = body.Length > 0 ? body + "…" : "";
        }
        return prefix + Escape(body) + suffix;
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

    /// <summary>
    /// Only the three characters Telegram treats as HTML. WebUtility.HtmlEncode is not used on purpose: it turns every
    /// Cyrillic letter and the non-breaking space in «1 400 ₽» into numeric entities, which Telegram would show as is.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Money(decimal amount) => amount.ToString("#,0.##", Ru);

    /// <summary>Wazzup wants digits only: +7 914 000-00-00 → 79140000000.</summary>
    private static string Digits(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length == 11 && digits[0] == '8' ? '7' + digits[1..] : digits;
    }
}
