using System.Text.RegularExpressions;

namespace MuzLoto.Web.Services;

/// <summary>Normalises customer contacts so staff and the Telegram sender always get one canonical form.</summary>
public static partial class ContactRules
{
    /// <summary>Russian mobile/landline number → "+7XXXXXXXXXX"; null when it is not a valid number.</summary>
    public static string? NormalizePhone(string? input)
    {
        var digits = new string((input ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length == 10) digits = "7" + digits;
        if (digits.Length != 11 || digits[0] is not ('7' or '8')) return null;
        return "+7" + digits[1..];
    }

    /// <summary>Accepts "name", "@name" or a t.me link and returns "@name"; null for e-mails and other text.</summary>
    public static string? NormalizeTelegram(string? input)
    {
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        value = TelegramLinkPrefix().Replace(value, string.Empty).TrimStart('@').TrimEnd('/');
        return TelegramUsername().IsMatch(value) ? "@" + value : null;
    }

    [GeneratedRegex(@"^(https?://)?(www\.)?(t\.me|telegram\.me)/", RegexOptions.IgnoreCase)]
    private static partial Regex TelegramLinkPrefix();

    // Telegram usernames: 5–32 chars, latin letters, digits and underscores, starting with a letter.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{4,31}$")]
    private static partial Regex TelegramUsername();
}
