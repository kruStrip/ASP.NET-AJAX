using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services.Messaging;

/// <param name="RetryAfter">The messenger asked to slow down (flood limits): try again after this delay.</param>
public sealed record SendOutcome(OutgoingMessageStatus Status, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static readonly SendOutcome Sent = new(OutgoingMessageStatus.Sent);
    public static SendOutcome Skipped(string why) => new(OutgoingMessageStatus.Skipped, why);
    public static SendOutcome Failed(string why, TimeSpan? retryAfter = null) => new(OutgoingMessageStatus.Failed, why, retryAfter);
}

/// <summary>One messenger account the site writes from (Telegram personal account, WhatsApp number).</summary>
public interface IMessengerChannel
{
    MessageChannel Channel { get; }
    /// <summary>Signed in and able to send right now.</summary>
    Task<bool> IsReadyAsync(CancellationToken ct);
    Task<SendOutcome> SendAsync(Booking booking, string text, CancellationToken ct);
}
