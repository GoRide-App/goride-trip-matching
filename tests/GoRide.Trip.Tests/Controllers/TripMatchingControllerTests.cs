using GoRide.Trip.Controllers;
using GoRide.Trip.Models;
using GoRide.Trip.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GoRide.Trip.Tests.Controllers;

public class TripMatchingControllerTests
{
    private readonly Mock<IDriverMatchingService> _matchingService = new();
    private readonly Mock<IRideRequestService> _rideRequestService = new();

    private TripMatchingController CreateController() =>
        new(_matchingService.Object, _rideRequestService.Object, NullLogger<TripMatchingController>.Instance);

    private static FindNearbyDriversRequest ValidRideRequest() => new()
    {
        TripId = "trip-123",
        RiderId = "rider-456",
        PickupLocation = "Colombo Fort",
        DropoffLocation = "Bambalapitiya",
        PickupLat = 6.9344,
        PickupLng = 79.8428,
        VehicleTypeCode = "TUK",
        DropoffLat = 6.8905,
        DropoffLng = 79.8565,
        Fare = 500m
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequestRide_MissingPickupLocation_Returns400(string? pickupLocation)
    {
        var request = ValidRideRequest();
        request.PickupLocation = pickupLocation;

        var result = await CreateController().RequestRide(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("pickupLocation", json);
        _rideRequestService.Verify(r => r.DeliverAsync(It.IsAny<FindNearbyDriversRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequestRide_MissingDropoffLocation_Returns400(string? dropoffLocation)
    {
        var request = ValidRideRequest();
        request.DropoffLocation = dropoffLocation;

        var result = await CreateController().RequestRide(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("dropoffLocation", json);
        _rideRequestService.Verify(r => r.DeliverAsync(It.IsAny<FindNearbyDriversRequest>()), Times.Never);
    }

    [Fact]
    public async Task RequestRide_MissingBothLocations_Returns400()
    {
        var request = ValidRideRequest();
        request.PickupLocation = null;
        request.DropoffLocation = null;

        var result = await CreateController().RequestRide(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("pickupLocation", json);
        _rideRequestService.Verify(r => r.DeliverAsync(It.IsAny<FindNearbyDriversRequest>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequestRide_MissingTripId_Returns400(string? tripId)
    {
        var request = ValidRideRequest();
        request.TripId = tripId;

        var result = await CreateController().RequestRide(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("tripId", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequestRide_MissingRiderId_Returns400(string? riderId)
    {
        var request = ValidRideRequest();
        request.RiderId = riderId;

        var result = await CreateController().RequestRide(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("riderId", json);
    }

    [Fact]
    public async Task RequestRide_ValidRequest_DeliversAndReturns200()
    {
        var request = ValidRideRequest();
        var expectedResponse = new FindNearbyDriversResponse
        {
            Matched = true,
            RadiusKm = 5,
            Drivers = [new MatchedDriver { DriverId = "d1", DistanceKm = 1.2, VehicleTypeCode = "TUK" }]
        };

        _rideRequestService.Setup(r => r.DeliverAsync(request))
            .ReturnsAsync((expectedResponse, (string?)null));

        var result = await CreateController().RequestRide(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);
        Assert.Same(expectedResponse, okResult.Value);
    }

    [Fact]
    public async Task RequestRide_DeliverConflict_Returns409()
    {
        var request = ValidRideRequest();
        _rideRequestService.Setup(r => r.DeliverAsync(request))
            .ReturnsAsync(((FindNearbyDriversResponse?)null, "This trip already has an accepted offer."));

        var result = await CreateController().RequestRide(request);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task FindNearbyDrivers_WithoutLocations_Succeeds()
    {
        var request = new FindNearbyDriversRequest
        {
            PickupLat = 6.9344,
            PickupLng = 79.8428,
            VehicleTypeCode = "TUK"
            // PickupLocation and DropoffLocation intentionally omitted
        };

        var expectedResponse = new FindNearbyDriversResponse { Matched = true, RadiusKm = 5 };
        _matchingService.Setup(m => m.FindNearbyDriversAsync(request))
            .ReturnsAsync(expectedResponse);

        var result = await CreateController().FindNearbyDrivers(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);
        Assert.Same(expectedResponse, okResult.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindNearbyDrivers_MissingVehicleTypeCode_Returns400(string? vehicleType)
    {
        var request = new FindNearbyDriversRequest
        {
            PickupLat = 6.9344,
            PickupLng = 79.8428,
            VehicleTypeCode = vehicleType!
        };

        var result = await CreateController().FindNearbyDrivers(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("vehicleTypeCode", json);
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    public async Task FindNearbyDrivers_OutOfRangeCoordinates_Returns400(double lat, double lng)
    {
        var request = new FindNearbyDriversRequest
        {
            PickupLat = lat,
            PickupLng = lng,
            VehicleTypeCode = "TUK"
        };

        var result = await CreateController().FindNearbyDrivers(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
    }
}
