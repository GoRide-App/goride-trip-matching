using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using GoRide.Trip.Data;
using GoRide.Trip.Events;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

namespace GoRide.Trip.Tests.Services;

public class RideRequestServiceTests
{
    private readonly Mock<IDriverMatchingService> _matching = new();
    private readonly Mock<IDriverOfferRepository> _offers = new();
    private readonly Mock<ITripEventPublisher> _publisher = new();
    private readonly Mock<IActiveDriversService> _activeDrivers = new();
    private readonly List<string> _callOrder = new();

    private RideRequestService CreateService() =>
        new(_matching.Object, _offers.Object, _publisher.Object, _activeDrivers.Object,
            Options.Create(new MatchingOptions { OfferTtlSeconds = 20 }),
            NullLogger<RideRequestService>.Instance);

    private static FindNearbyDriversRequest Request() => new()
    {
        TripId = "trip-1",
        RiderId = "rider-1",
        PickupLat = 6.9344,
        PickupLng = 79.8428,
        VehicleTypeCode = "TUK",
        PickupLocation = "Colombo Fort",
        DropoffLocation = "Bambalapitiya",
        Fare = 480m,
    };

    private static MatchedDriver Driver(string id, double distanceKm = 1) =>
        new() { DriverId = id, VehicleTypeCode = "TUK", DistanceKm = distanceKm };

    private void MatchingReturns(params MatchedDriver[] drivers) =>
        _matching.Setup(m => m.FindNearbyDriversAsync(It.IsAny<FindNearbyDriversRequest>()))
            .ReturnsAsync(new FindNearbyDriversResponse { Matched = drivers.Length > 0, RadiusKm = 5, Drivers = drivers.ToList() });

    [Fact]
    public async Task TripAlreadyAccepted_ReturnsError_AndSendsNothing()
    {
        _offers.Setup(o => o.HasAcceptedOfferAsync("trip-1")).ReturnsAsync(true);

        var (result, error) = await CreateService().DeliverAsync(Request());

        Assert.Null(result);
        Assert.NotNull(error);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<TripEvent>()), Times.Never);
        _matching.Verify(m => m.FindNearbyDriversAsync(It.IsAny<FindNearbyDriversRequest>()), Times.Never);
    }

    [Fact]
    public async Task NoMatchingDrivers_ReturnsNotMatched_AndSendsNothing()
    {
        MatchingReturns();

        var (result, error) = await CreateService().DeliverAsync(Request());

        Assert.Null(error);
        Assert.False(result!.Matched);
        _offers.Verify(o => o.CreatePendingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<decimal?>()), Times.Never);
        _publisher.Verify(p => p.PublishAsync(It.IsAny<TripEvent>()), Times.Never);
    }

    [Fact]
    public async Task MatchedDrivers_EachGetAPendingOfferAndARideRequestedEvent()
    {
        MatchingReturns(Driver("d1", 0.4), Driver("d2", 1.2));
        var published = new List<TripEvent>();
        _publisher.Setup(p => p.PublishAsync(It.IsAny<TripEvent>()))
            .Callback<TripEvent>(published.Add).Returns(Task.CompletedTask);

        var (result, error) = await CreateService().DeliverAsync(Request());

        Assert.Null(error);
        Assert.True(result!.Matched);
        Assert.Equal(new[] { "d1", "d2" }, result.Drivers.Select(d => d.DriverId));

        // The trip details are stored on the offer so the driver can be shown them later.
        _offers.Verify(o => o.CreatePendingAsync("trip-1", "d1", "rider-1", 0.4, "Colombo Fort", "Bambalapitiya", 480m), Times.Once);
        _offers.Verify(o => o.CreatePendingAsync("trip-1", "d2", "rider-1", 1.2, "Colombo Fort", "Bambalapitiya", 480m), Times.Once);

        Assert.Equal(2, published.Count);
        var evt = published.Single(e => e.DriverId == "d1");
        Assert.Equal("RIDE_REQUESTED", evt.EventType);
        Assert.Equal("trip-1", evt.TripId);
        Assert.Equal("rider-1", evt.RiderId);
        Assert.Equal("Colombo Fort", evt.Payload.PickupLocation);
        Assert.Equal("Bambalapitiya", evt.Payload.DropoffLocation);
        Assert.Equal(480m, evt.Payload.Fare);
        Assert.Equal("TUK", evt.Payload.VehicleType);
        Assert.NotEqual(published[0].EventId, published[1].EventId);   // notification dedupes on EventId
    }

    [Fact]
    public async Task OfferIsRecorded_BeforeTheEventIsPublished()
    {
        MatchingReturns(Driver("d1"));
        _offers.Setup(o => o.CreatePendingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<decimal?>()))
            .Callback(() => _callOrder.Add("offer")).Returns(Task.CompletedTask);
        _publisher.Setup(p => p.PublishAsync(It.IsAny<TripEvent>()))
            .Callback(() => _callOrder.Add("publish")).Returns(Task.CompletedTask);

        await CreateService().DeliverAsync(Request());

        Assert.Equal(new[] { "offer", "publish" }, _callOrder);
    }

    [Fact]
    public async Task FailedPublish_DoesNotFailTheRequest_TheDriverStillHasTheOffer()
    {
        // The offer row is what the driver's app reads, so a Kafka outage must not stop matching.
        MatchingReturns(Driver("d1"), Driver("d2"));
        _publisher.Setup(p => p.PublishAsync(It.Is<TripEvent>(e => e.DriverId == "d1"))).ThrowsAsync(new Exception("kafka down"));
        _publisher.Setup(p => p.PublishAsync(It.Is<TripEvent>(e => e.DriverId == "d2"))).Returns(Task.CompletedTask);

        var (result, error) = await CreateService().DeliverAsync(Request());

        Assert.Null(error);
        Assert.True(result!.Matched);
        Assert.Equal(new[] { "d1", "d2" }, result.Drivers.Select(d => d.DriverId));
        _offers.Verify(o => o.CreatePendingAsync("trip-1", "d1", "rider-1", It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<decimal?>()), Times.Once);
    }

    [Fact]
    public async Task KafkaCompletelyDown_StillMatchesAndOffers()
    {
        MatchingReturns(Driver("d1"), Driver("d2"));
        _publisher.Setup(p => p.PublishAsync(It.IsAny<TripEvent>())).ThrowsAsync(new Exception("kafka down"));

        var (result, error) = await CreateService().DeliverAsync(Request());

        Assert.Null(error);
        Assert.True(result!.Matched);
        Assert.Equal(2, result.Drivers.Count);
    }

    [Fact]
    public async Task OfferCannotBeRecorded_TheRequestFails()
    {
        // Without the offer row no driver could ever see or accept the request, so this must surface.
        MatchingReturns(Driver("d1"));
        _offers.Setup(o => o.CreatePendingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<decimal?>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().DeliverAsync(Request()));
    }

    [Fact]
    public void TripEvent_SerializesToTheJsonShapeNotificationExpects()
    {
        var json = JsonSerializer.Serialize(new TripEvent
        {
            EventType = "RIDE_REQUESTED",
            TripId = "t",
            RiderId = "r",
            DriverId = "d",
            Payload = new TripEventPayload { PickupLocation = "Fort" },
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("RIDE_REQUESTED", root.GetProperty("eventType").GetString());
        Assert.Equal("t", root.GetProperty("tripId").GetString());
        Assert.Equal("r", root.GetProperty("riderId").GetString());
        Assert.Equal("d", root.GetProperty("driverId").GetString());
        Assert.True(root.TryGetProperty("eventId", out _));
        Assert.Equal("Fort", root.GetProperty("payload").GetProperty("pickupLocation").GetString());
    }

    // ---- GetStatusAsync (rider polls this) ----

    private static DriverOffer OfferWith(string driverId, string status, DateTime createdAt) =>
        new() { TripId = "trip-1", DriverId = driverId, Status = status, CreatedAt = createdAt };

    [Fact]
    public async Task Status_NoOffersEver_ReturnsNull()
    {
        _offers.Setup(o => o.GetOffersForTripAsync("trip-1")).ReturnsAsync(new List<DriverOffer>());

        Assert.Null(await CreateService().GetStatusAsync("trip-1"));
    }

    [Fact]
    public async Task Status_OpenPendingOffer_IsSearching()
    {
        _offers.Setup(o => o.GetOffersForTripAsync("trip-1")).ReturnsAsync(new List<DriverOffer>
        {
            OfferWith("d1", "Pending", DateTime.UtcNow.AddSeconds(-5)),
        });

        var status = await CreateService().GetStatusAsync("trip-1");

        Assert.Equal("Searching", status!.Status);
        Assert.Null(status.Driver);
    }

    [Fact]
    public async Task Status_AllOffersLapsedOrFailed_IsNoDriver()
    {
        _offers.Setup(o => o.GetOffersForTripAsync("trip-1")).ReturnsAsync(new List<DriverOffer>
        {
            OfferWith("d1", "Pending", DateTime.UtcNow.AddSeconds(-60)),   // Pending but past its 20s window
            OfferWith("d2", "Declined", DateTime.UtcNow.AddSeconds(-2)),
        });

        Assert.Equal("NoDriver", (await CreateService().GetStatusAsync("trip-1"))!.Status);
    }

    [Fact]
    public async Task Status_AcceptedOffer_ReturnsTheDriverWithVehicleDetails()
    {
        _offers.Setup(o => o.GetOffersForTripAsync("trip-1")).ReturnsAsync(new List<DriverOffer>
        {
            OfferWith("d1", "Accepted", DateTime.UtcNow.AddSeconds(-8)),
            OfferWith("d2", "Pending", DateTime.UtcNow.AddSeconds(-8)),
        });
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ReturnsAsync(new List<ActiveDriver>
        {
            new() { DriverId = "d1", VehiclePlate = "CAB-4521", VehicleTypeCode = "TUK", VehicleMake = "Bajaj", VehicleModel = "RE" },
        });

        var status = await CreateService().GetStatusAsync("trip-1");

        Assert.Equal("Accepted", status!.Status);
        Assert.Equal("d1", status.Driver!.DriverId);
        Assert.Equal("CAB-4521", status.Driver.VehiclePlate);
    }

    [Fact]
    public async Task Status_AcceptedButIdentityDown_StillReportsTheDriverId()
    {
        _offers.Setup(o => o.GetOffersForTripAsync("trip-1")).ReturnsAsync(new List<DriverOffer>
        {
            OfferWith("d1", "Accepted", DateTime.UtcNow),
        });
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ThrowsAsync(new HttpRequestException("identity down"));

        var status = await CreateService().GetStatusAsync("trip-1");

        Assert.Equal("Accepted", status!.Status);
        Assert.Equal("d1", status.Driver!.DriverId);
    }
}
