using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MuzLoto.Web.Services;

/// <summary>
/// Public, unguessable link to a customer's uploaded receipt. The file itself stays outside wwwroot and is served
/// only through an encrypted, time-limited token: the messenger service (Wazzup) fetches the file by URL when the
/// confirmation message duplicates the receipt, and the customer can open it too. The link is never listed anywhere.
/// </summary>
public sealed class ReceiptLinks(IDataProtectionProvider provider, IConfiguration config)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private ITimeLimitedDataProtector Protector =>
        provider.CreateProtector("MuzLoto.Receipt.v1").ToTimeLimitedDataProtector();

    /// <summary>Absolute URL with the real file extension, so the messenger sees whether it is a photo or a PDF.</summary>
    public string Create(Guid bookingId, string receiptKey)
    {
        var site = (config["Site:PublicUrl"] ?? "https://muzloto.b-o-c-a.ru").TrimEnd('/');
        var token = Uri.EscapeDataString(Protector.Protect(bookingId.ToString("N"), Lifetime));
        var name = Uri.EscapeDataString(Path.GetFileName(receiptKey));
        return $"{site}/receipts/{bookingId:N}/{name}?t={token}";
    }

    /// <summary>The booking ID the token was issued for, or null for a missing, expired or forged token.</summary>
    public Guid? Read(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try { return Guid.TryParseExact(Protector.Unprotect(token), "N", out var id) ? id : null; }
        catch (CryptographicException) { return null; }
    }
}
