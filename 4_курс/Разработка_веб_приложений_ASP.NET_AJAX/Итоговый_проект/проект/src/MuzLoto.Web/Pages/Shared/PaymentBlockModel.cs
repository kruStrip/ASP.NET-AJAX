using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Shared;

/// <param name="UploadUrl">Where the receipt form posts (the page's own «Receipt» handler).</param>
/// <param name="CanViewReceipt">The viewer is signed in as the owner, so the receipt link works for them.</param>
public sealed record PaymentBlockModel(Booking Booking, Domain.PaymentSettings? Settings, string UploadUrl, bool CanViewReceipt);
