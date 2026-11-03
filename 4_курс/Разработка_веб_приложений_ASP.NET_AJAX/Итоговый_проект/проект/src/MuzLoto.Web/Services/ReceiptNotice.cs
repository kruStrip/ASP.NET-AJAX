using Microsoft.AspNetCore.SignalR;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Hubs;

namespace MuzLoto.Web.Services;

/// <summary>
/// A customer uploaded a receipt: queue the managers' Telegram notification (with the file and a link to the
/// confirmation card) and refresh the staff lists of the game live.
/// </summary>
public static class ReceiptNotice
{
    public static async Task SendAsync(IHubContext<BookingHub> hub, BookingWorkflow workflow, Booking booking,
        CancellationToken ct = default)
    {
        // The notification is queued first: it must not be lost if the live refresh fails.
        await workflow.QueueReceiptNotificationAsync(booking.Id, ct);
        try { await hub.Clients.Group(BookingHub.GroupFor(booking.GameSessionId)).SendCoreAsync("BookingUpdated", new object[] { booking.Id }); }
        catch { /* the receipt is saved; a missed live refresh is shown on the next page load */ }
    }
}
