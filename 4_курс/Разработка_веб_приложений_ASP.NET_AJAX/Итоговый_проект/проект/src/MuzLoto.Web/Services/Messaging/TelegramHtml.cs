namespace MuzLoto.Web.Services.Messaging;

/// <summary>
/// The little HTML that Telegram messages use (parse_mode=HTML): escaping and a phone a manager can copy with one
/// tap. WebUtility.HtmlEncode is not used on purpose: it turns every Cyrillic letter and the non-breaking space in
/// «1 400 ₽» into numeric entities, which Telegram would show as is.
/// </summary>
public static class TelegramHtml
{
    /// <summary>Only the three characters Telegram treats as HTML.</summary>
    public static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// Digits inside &lt;code&gt;: Telegram makes such a value tappable (a copy action on mobile), so a manager does
    /// not have to select the phone by hand. <paramref name="escapedText"/> must already be escaped for HTML.
    /// </summary>
    public static string CodePhone(string escapedText, string? phone)
    {
        var digits = Digits(phone);
        if (digits.Length == 0) return escapedText;
        // The messages print the normalized digits, but a phone typed with «+7» is still handled.
        var shown = Escape(phone!.Trim());
        if (shown.Length > 0 && shown != digits)
            escapedText = escapedText.Replace($"Телефон: {shown}", $"Телефон: <code>{shown}</code>");
        return escapedText.Replace($"Телефон: {digits}", $"Телефон: <code>{digits}</code>");
    }

    /// <summary>Escapes a plain message and makes its phone copyable, keeping it within Telegram's length limit.</summary>
    public static string ToHtml(string text, string? phone, int maxLength = 1024)
    {
        var html = CodePhone(Escape(text), phone);
        if (html.Length <= maxLength) return html;
        // The ellipsis takes a character, so the source is shortened until the escaped text fits.
        var plain = text;
        do { plain = plain[..^1].TrimEnd(); }
        while (plain.Length > 0 && CodePhone(Escape(plain + "…"), phone).Length > maxLength);
        return CodePhone(Escape(plain.Length > 0 ? plain + "…" : ""), phone);
    }

    /// <summary>Wazzup wants digits only: +7 914 000-00-00 → 79140000000.</summary>
    public static string Digits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length == 11 && digits[0] == '8' ? '7' + digits[1..] : digits;
    }
}
