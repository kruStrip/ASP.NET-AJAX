using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

public static class CapacityCalculator
{
    // Pending and Cancelled requests do NOT reserve seats.
    public static int Remaining(int capacity, IEnumerable<Booking> bookings) =>
        capacity - bookings.Where(x => x.Status == BookingStatus.Confirmed).Sum(x => x.Quantity);

    public static bool CanConfirm(int capacity, int reserved, int quantity) =>
        capacity >= 0 && reserved >= 0 && quantity > 0 && (long)reserved + quantity <= capacity;
}
