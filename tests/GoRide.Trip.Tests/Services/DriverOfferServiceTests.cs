using Microsoft.Extensions.Caching.Memory;
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
    private readonly Mock<ITripCompletionNotifier> _completionNotifier = new();

    // A real (fresh, empty) cache in front of the mocked identity-auth call, as in production.
    private DriverOfferService CreateService() =>
        new(_offers.Object, _publisher.Object,
            new DriverProfileLookup(_activeDrivers.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<DriverProfileLookup>.Instance),
            _completionNotifier.Object,
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

    [Fact]
    public async Task Accept_DriverAlreadyOnAnotherTrip_IsDriverBusy_AndTheTripIsNotClaimed()
    {
        _offers.Setup(o => o.HasActiveTripAsync("d1", "trip-2")).ReturnsAsync(true);

        var (outcome, offer) = await CreateService().AcceptAsync("trip-2", "d1");

        Assert.Equal(AcceptOutcome.DriverBusy, outcome);
        Assert.Null(offer);
        // Never claimed, so the other drivers it was offered to can still take it.
        _offers.Verify(o => o.TryAcceptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<TripEvent>()), Times.Never);
    }

    [Fact]
    public async Task Accept_TheBusyCheckIgnoresTheTripBeingAccepted()
    {
        // Otherwise a retried accept of the driver's own trip would be reported as "busy".
        _offers.Setup(o => o.TryAcceptAsync("trip-1", "d1", Ttl)).ReturnsAsync(AcceptedOffer());

        var (outcome, _) = await CreateService().AcceptAsync("trip-1", "d1");

        Assert.Equal(AcceptOutcome.Accepted, outcome);
        _offers.Verify(o => o.HasActiveTripAsync("d1", "trip-1"), Times.Once);
    }

    // ---- GetCurrentTripAsync (driver app restoring its trip) ----

    [Fact]
    public async Task CurrentTrip_ReturnsTheTrip_InTheSameShapeAcceptReturns()
    {
        var trip = AcceptedOffer();
        trip.Status = "InProgress";
        _offers.Setup(o => o.GetCurrentTripForDriverAsync("d1", 6)).ReturnsAsync(trip);

        var current = await CreateService().GetCurrentTripAsync("d1");

        Assert.Equal("InProgress", current!.Status);
        Assert.Equal(trip.CreatedAt.AddSeconds(Ttl), current.ExpiresAt);
    }

    [Fact]
    public async Task CurrentTrip_None_ReturnsNull()
    {
        _offers.Setup(o => o.GetCurrentTripForDriverAsync("d1", It.IsAny<int>())).ReturnsAsync((DriverOffer?)null);

        Assert.Null(await CreateService().GetCurrentTripAsync("d1"));
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

    [Fact]
    public async Task UpdateStatus_SkippingAStage_IsRejectedAsInvalidTransition()
    {
        // Trying to jump straight to InProgress from Accepted (skipping Arrived).
        _offers.Setup(o => o.TryAdvanceStatusAsync("trip-1", "d1", "Arrived", "InProgress")).ReturnsAsync((DriverOffer?)null);
        _offers.Setup(o => o.GetStatusAsync("trip-1", "d1")).ReturnsAsync("Accepted");

        var (outcome, offer) = await CreateService().UpdateStatusAsync("trip-1", "d1", "InProgress");

        Assert.Equal(StatusUpdateOutcome.InvalidTransition, outcome);
        Assert.Null(offer);
    }

    [Fact]
    public async Task UpdateStatus_NoSuchOffer_ReturnsNotFound()
    {
        _offers.Setup(o => o.TryAdvanceStatusAsync("trip-1", "d1", "Accepted", "Arrived")).ReturnsAsync((DriverOffer?)null);
        _offers.Setup(o => o.GetStatusAsync("trip-1", "d1")).ReturnsAsync((string?)null);

        var (outcome, _) = await CreateService().UpdateStatusAsync("trip-1", "d1", "Arrived");

        Assert.Equal(StatusUpdateOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task UpdateStatus_UnknownAction_IsRejectedWithoutTouchingTheRepository()
    {
        var (outcome, offer) = await CreateService().UpdateStatusAsync("trip-1", "d1", "Cancelled");

        Assert.Equal(StatusUpdateOutcome.InvalidTransition, outcome);
        Assert.Null(offer);
        _offers.Verify(o => o.TryAdvanceStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ---- Completion -> payment + TRIP_COMPLETED ----

    private static DriverOffer CompletedTrip(decimal? fare = 725.50m)
    {
        var trip = AcceptedOffer();
        trip.Status = "Completed";
        trip.Fare = fare;
        trip.CompletedAt = new DateTime(2026, 9, 20, 10, 25, 0, DateTimeKind.Utc);
        return trip;
    }

    [Fact]
    public async Task UpdateStatus_Completed_TellsPayment_AndQueuesTripCompleted()
    {
        var completed = CompletedTrip();
        _offers.Setup(o => o.TryAdvanceStatusAsync("trip-1", "d1", "InProgress", "Completed")).ReturnsAsync(completed);
        TripEvent? published = null;
        _publisher.Setup(p => p.PublishAsync(It.IsAny<TripEvent>()))
            .Callback<TripEvent>(e => published = e).Returns(Task.CompletedTask);

        var (outcome, _) = await CreateService().UpdateStatusAsync("trip-1", "d1", "Completed");

        Assert.Equal(StatusUpdateOutcome.Updated, outcome);
        _completionNotifier.Verify(n => n.Enqueue(completed), Times.Once);
        Assert.Equal("TRIP_COMPLETED", published!.EventType);
        Assert.Equal("trip-1", published.TripId);
        Assert.Equal("rider-1", published.RiderId);
        Assert.Equal("d1", published.DriverId);
        Assert.Equal(completed.CompletedAt, published.OccurredAt);
        Assert.Equal(725.50m, published.Payload.Fare);
        Assert.NotEqual("trip-completed-trip-1", published.EventId);   // payment-service's direct eventId is kept separate
    }

    [Fact]
    public async Task UpdateStatus_CompletedWithoutAFare_PublishesNoTripCompleted()
    {
        _offers.Setup(o => o.TryAdvanceStatusAsync("trip-1", "d1", "InProgress", "Completed")).ReturnsAsync(CompletedTrip(fare: null));

        var (outcome, _) = await CreateService().UpdateStatusAsync("trip-1", "d1", "Completed");

        Assert.Equal(StatusUpdateOutcome.Updated, outcome);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<TripEvent>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_Completed_StillSucceeds_WhenNotifyingFails()
    {
        _offers.Setup(o => o.TryAdvanceStatusAsync("trip-1", "d1", "InProgress", "Completed")).ReturnsAsync(CompletedTrip());
        _completionNotifier.Setup(n => n.Enqueue(It.IsAny<DriverOffer>())).Throws(new InvalidOperationException("boom"));

        var (outcome, _) = await CreateService().UpdateStatusAsync("trip-1", "d1", "Completed");

        Assert.Equal(StatusUpdateOutcome.Updated, outcome);
    }

    [Theory]
    [InlineData("Arrived", "Accepted")]
    [InlineData("InProgress", "Arrived")]
    public async Task UpdateStatus_EarlierStages_DoNotTellPayment(string action, string fromStatus)
    {
        var updated = AcceptedOffer();
        updated.Status = action;
        _offers.Setup(o => o.TryAdvanceStatusAsync("trip-1", "d1", fromStatus, action)).ReturnsAsync(updated);

        await CreateService().UpdateStatusAsync("trip-1", "d1", action);

        _completionNotifier.Verify(n => n.Enqueue(It.IsAny<DriverOffer>()), Times.Never);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<TripEvent>()), Times.Never);
    }
}
