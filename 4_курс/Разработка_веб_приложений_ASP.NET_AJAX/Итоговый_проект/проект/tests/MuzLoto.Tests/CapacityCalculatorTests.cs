using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;
using Xunit;

namespace MuzLoto.Tests;

public class CapacityCalculatorTests
{
    [Fact]
    public void PendingRequestsDoNotReserveSeats()
    {
        var bookings = new[] { new Booking { Quantity = 8, Status = BookingStatus.Pending },
            new Booking { Quantity = 2, Status = BookingStatus.Confirmed } };
        Assert.Equal(8, CapacityCalculator.Remaining(10, bookings));
    }

    [Fact]
    public void CancelledBookingsDoNotReserveSeats()
    {
        var bookings = new[] { new Booking { Quantity = 4, Status = BookingStatus.Cancelled },
            new Booking { Quantity = 3, Status = BookingStatus.Confirmed } };
        Assert.Equal(7, CapacityCalculator.Remaining(10, bookings));
    }

    [Theory]
    [InlineData(10, 6, 4, true)]
    [InlineData(10, 6, 5, false)]
    [InlineData(10, 10, 1, false)]
    [InlineData(10, 0, 0, false)]
    public void ConfirmationRespectsCapacity(int capacity, int reserved, int quantity, bool expected)
    {
        Assert.Equal(expected, CapacityCalculator.CanConfirm(capacity, reserved, quantity));
    }
}
