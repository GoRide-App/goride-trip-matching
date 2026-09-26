using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using GoRide.Trip.Data;
using GoRide.Trip.Events;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

namespace GoRide.Trip.Tests.Services;

public class DriverOfferServiceTests
{
    private const int Ttl = 20;

    private readonly Mock<IDriverOfferRepository> _offers = new();
    private readonly Mock<ITripEventPublisher> _publisher = new();
    private readonly Mock<IActiveDriversService> _activeDrivers = new();

    private DriverOfferService CreateService() =>
        new(_offers.Object, _publisher.Object, _activeDrivers.Object,
            Options.Create(new MatchingOptions { OfferTtlSeconds = Ttl }),
            NullLogger<DriverOfferService>.Instance);

    private static DriverOffer AcceptedOffer() => new()
    {
        TripId = "trip-1",
        DriverId = "d1",
        RiderId = "rider-1",
        Status = "Accepted",
        PickupLocation = "Colombo Fort",
        DropoffLocation = "Bambalapitiya",
        Fare = 480m,
        CreatedAt = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc),
    };

    // ---- GetPendingAsync (SCRUM-59) ----

    [Fact]
    public async Task GetPending_ReturnsTheDriversOffers_WithExpiryFromTheTtl()
    {
        var created = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        _offers.Setup(o => o.GetPendingForDriverAsync("d1", Ttl))
            .ReturnsAsync(new List<DriverOffer> { new() { TripId = "t1", DriverId = "d1", CreatedAt = created } });

        var result = await CreateService().GetPendingAsync("d1");

        Assert.Single(result);
        Assert.Equal(created.AddSeconds(Ttl), result[0].ExpiresAt);
    }

    [Fact]
    public async Task GetPending_NoOffers_ReturnsEmptyList()
    {
        _offers.Setup(o => o.GetPendingForDriverAsync("d1", Ttl)).ReturnsAsync(new List<DriverOffer>());

        Assert.Empty(await CreateService().GetPendingAsync("d1"));
    }

    // ---- AcceptAsync (SCRUM-60) ----

    [Fact]
    public async Task Accept_ValidOffer_ReturnsAccepted_AndPublishesDriverAcceptedToTheRider()
    {
        _offers.Setup(o => o.TryAcceptAsync("trip-1", "d1", Ttl)).ReturnsAsync(AcceptedOffer());
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ReturnsAsync(new List<ActiveDriver>
        {
            new() { DriverId = "someone-else", VehiclePlate = "XXX" },
            new() { DriverId = "d1", VehiclePlate = "CAB-4521", VehicleTypeCode = "TUK" },
        });
        TripEvent? published = null;
        _publisher.Setup(p => p.PublishAsync(It.IsAny<TripEvent>()))
            .Callback<TripEvent>(e => published = e).Returns(Task.CompletedTask);

        var (outcome, offer) = await CreateService().AcceptAsync("trip-1", "d1");

        Assert.Equal(AcceptOutcome.Accepted, outcome);
        Assert.Equal("trip-1", offer!.TripId);

        Assert.NotNull(published);
        Assert.Equal("DRIVER_ACCEPTED", published!.EventType);
        Assert.Equal("trip-1", published.TripId);
        Assert.Equal("rider-1", published.RiderId);          // the rider is who gets notified
        Assert.Equal("d1", published.DriverId);
        Assert.Equal("CAB-4521", published.Payload.VehiclePlate);
        Assert.Equal("TUK", published.Payload.VehicleType);
        Assert.Equal(480m, published.Payload.Fare);
        Assert.Equal("Colombo Fort", published.Payload.PickupLocation);
    }

    [Fact]
    public async Task Accept_NoSuchOffer_ReturnsNotFound_AndPublishesNothing()
    {
        _offers.Setup(o => o.TryAcceptAsync("trip-1", "d1", Ttl)).ReturnsAsync((DriverOffer?)null);
        _offers.Setup(o => o.GetStatusAsync("trip-1", "d1")).ReturnsAsync((string?)null);

        var (outcome, offer) = await CreateService().AcceptAsync("trip-1", "d1");

        Assert.Equal(AcceptOutcome.NotFound, outcome);
        Assert.Null(offer);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<TripEvent>()), Times.Never);
    }

    [Theory]
    [InlineData("Pending")]     // still Pending but TryAccept refused it => it has expired
    [InlineData("Accepted")]
    [InlineData("Declined")]
    [InlineData("Failed")]
    public async Task Accept_OfferExistsButCannotBeAccepted_ReturnsNotAvailable_AndPublishesNothing(string status)
    {
        _offers.Setup(o => o.TryAcceptAsync("trip-1", "d1", Ttl)).ReturnsAsync((DriverOffer?)null);
        _offers.Setup(o => o.GetStatusAsync("trip-1", "d1")).ReturnsAsync(status);

        var (outcome, _) = await CreateService().AcceptAsync("trip-1", "d1");

        Assert.Equal(AcceptOutcome.NotAvailable, outcome);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<TripEvent>()), Times.Never);
    }

    [Fact]
    public async Task Accept_LosingDriverOfARaceToBeAccepted_ReturnsNotAvailable()
    {
        // SCRUM-62: two drivers accept the same trip at the same instant. The repository's
        // TryAcceptAsync is what actually decides the race (via the trip_claims table's PRIMARY
        // KEY on trip_id) -- from here, a driver who lost that race looks exactly like any other
        // "couldn't accept": TryAcceptAsync returns null, and the offer is already Accepted by
        // whoever won when we look it up to explain why.
        _offers.Setup(o => o.TryAcceptAsync("trip-1", "d2", Ttl)).ReturnsAsync((DriverOffer?)null);
        _offers.Setup(o => o.GetStatusAsync("trip-1", "d2")).ReturnsAsync("Accepted");

        var (outcome, offer) = await CreateService().AcceptAsync("trip-1", "d2");

        Assert.Equal(AcceptOutcome.NotAvailable, outcome);
        Assert.Null(offer);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<TripEvent>()), Times.Never);
    }

    [Fact]
    public async Task Accept_StillSucceeds_WhenTheEventCannotBePublished()
    {
        // The accept is already committed; failing to notify the rider must not undo or hide it.
        _offers.Setup(o => o.TryAcceptAsync("trip-1", "d1", Ttl)).ReturnsAsync(AcceptedOffer());
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ReturnsAsync(new List<ActiveDriver>());
        _publisher.Setup(p => p.PublishAsync(It.IsAny<TripEvent>())).ThrowsAsync(new Exception("kafka down"));

        var (outcome, _) = await CreateService().AcceptAsync("trip-1", "d1");

        Assert.Equal(AcceptOutcome.Accepted, outcome);
    }

    [Fact]
    public async Task Accept_StillPublishes_WhenTheVehicleLookupFails()
    {
        _offers.Setup(o => o.TryAcceptAsync("trip-1", "d1", Ttl)).ReturnsAsync(AcceptedOffer());
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ThrowsAsync(new HttpRequestException("identity down"));
        TripEvent? published = null;
        _publisher.Setup(p => p.PublishAsync(It.IsAny<TripEvent>()))
            .Callback<TripEvent>(e => published = e).Returns(Task.CompletedTask);

        var (outcome, _) = await CreateService().AcceptAsync("trip-1", "d1");

        Assert.Equal(AcceptOutcome.Accepted, outcome);
        Assert.NotNull(published);
        Assert.Null(published!.Payload.VehiclePlate);   // just left blank
    }

    // ---- UpdateStatusAsync (SCRUM-82) ----

    [Theory]
    [InlineData("Arrived", "Accepted")]
    [InlineData("InProgress", "Arrived")]
    [InlineData("Completed", "InProgress")]
    public async Task UpdateStatus_ValidNextStage_Advances(string action, string fromStatus)
    {
        var updated = AcceptedOffer();
        updated.Status = action;
        _offers.Setup(o => o.TryAdvanceStatusAsync("trip-1", "d1", fromStatus, action)).ReturnsAsync(updated);

        var (outcome, offer) = await CreateService().UpdateStatusAsync("trip-1", "d1", action);

        Assert.Equal(StatusUpdateOutcome.Updated, outcome);
        Assert.Equal(action, offer!.Status);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Accepted")]
    [InlineData("Declined")]
    [InlineData("Expired")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    public async Task UpdateStatus_InvalidStart_IsRejectedWithoutNotifying(string currentStatus)
    {
        // Trying to jump straight to InProgress from Accepted (skipping Arrived).
        _offers.Setup(o => o.TryAdvanceStatusAsync("trip-1", "d1", "Arrived", "InProgress")).ReturnsAsync((DriverOffer?)null);
        _offers.Setup(o => o.GetStatusAsync("trip-1", "d1")).ReturnsAsync(currentStatus);

        var (outcome, offer) = await CreateService().UpdateStatusAsync("trip-1", "d1", "InProgress");

        Assert.Equal(StatusUpdateOutcome.InvalidTransition, outcome);
        Assert.Null(offer);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<TripEvent>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_NoSuchOffer_ReturnsNotFound()
    {
        _offers.Setup(o => o.TryAdvanceStatusAsync("trip-1", "d1", "Accepted", "Arrived")).ReturnsAsync((DriverOffer?)null);
        _offers.Setup(o => o.GetStatusAsync("trip-1", "d1")).ReturnsAsync((string?)null);

        var (outcome, _) = await CreateService().UpdateStatusAsync("trip-1", "d1", "Arrived");

        Assert.Equal(StatusUpdateOutcome.NotFound, outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Cancelled")]
    [InlineData("inprogress")]
    public async Task UpdateStatus_UnknownAction_IsRejectedWithoutTouchingTheRepository(string? action)
    {
        var (outcome, offer) = await CreateService().UpdateStatusAsync("trip-1", "d1", action!);

        Assert.Equal(StatusUpdateOutcome.InvalidTransition, outcome);
        Assert.Null(offer);
        _offers.Verify(o => o.TryAdvanceStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null, "d1")]
    [InlineData("", "d1")]
    [InlineData(" ", "d1")]
    [InlineData("trip-1", null)]
    [InlineData("trip-1", "")]
    [InlineData("trip-1", " ")]
    public async Task UpdateStatus_MissingId_IsRejectedBeforeAccessingTheStore(string? tripId, string? driverId)
    {
        var (outcome, offer) = await CreateService().UpdateStatusAsync(tripId!, driverId!, "InProgress");

        Assert.Equal(StatusUpdateOutcome.InvalidRequest, outcome);
        Assert.Null(offer);
        _offers.VerifyNoOtherCalls();
        _publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateStatus_OverlongId_IsRejectedBeforeAccessingTheStore(bool longTripId)
    {
        var longId = new string('x', 65);
        var (outcome, _) = await CreateService().UpdateStatusAsync(
            longTripId ? longId : "trip-1", longTripId ? "d1" : longId, "InProgress");

        Assert.Equal(StatusUpdateOutcome.InvalidRequest, outcome);
        _offers.VerifyNoOtherCalls();
    }
}
