using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

/// <summary>Staff search across bookings by team, name, phone (any formatting) or booking ID.</summary>
public static class BookingSearch
{
    public static IQueryable<Booking> Apply(IQueryable<Booking> query, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return query;
        var term = text.Trim();
        var like = "%" + Escape(term) + "%";
        var digits = new string(term.Where(char.IsDigit).ToArray());
        // "8 (900) 123" finds "+7900123..." too: digits are matched in order, ignoring separators,
        // and a leading 7/8 country prefix is dropped.
        if (digits.Length == 11 && digits[0] is '7' or '8') digits = digits[1..];
        var phoneLike = digits.Length >= 4 ? "%" + string.Join("%", digits.ToCharArray()) + "%" : null;
        return query.Where(x =>
            EF.Functions.ILike(x.TeamName, like) ||
            EF.Functions.ILike(x.CustomerName, like) ||
            EF.Functions.ILike(x.Id.ToString(), term + "%") ||
            (phoneLike != null && EF.Functions.ILike(x.Phone, phoneLike)));
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
