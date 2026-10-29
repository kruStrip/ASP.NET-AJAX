namespace MuzLoto.Web.Services;

public static class LinkRules
{
    /// <summary>Only absolute http(s) links are stored or rendered, so "javascript:" can never become a clickable link.</summary>
    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
