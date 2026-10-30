using Microsoft.AspNetCore.SignalR;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Hubs;

namespace MuzLoto.Web.Services;

/// <summary>Staff lists of the game refresh live when a customer uploads a receipt.</summary>
public static class ReceiptNotice
{
    public static async Task SendAsync(IHubContext<BookingHub> hub, Booking booking)
    {
        try { await hub.Clients.Group(BookingHub.GroupFor(booking.GameSessionId)).SendCoreAsync("BookingUpdated", new object[] { booking.Id }); }
        catch { /* the receipt is saved; a missed live refresh is shown on the next page load */ }
    }
}
