using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

namespace GoRide.Trip.Tests.Services;

public class DriverMatchingServiceTests
{
    // Pickup near Colombo Fort; ~0.001 deg lat is roughly 111 m.
    private const double PickupLat = 6.9344;
    private const double PickupLng = 79.8428;

    private static DriverMatchingService CreateService(
        List<NearbyDriverLocation> nearby,
        List<ActiveDriver> vehicleDrivers,
        int maxDrivers = 10)
    {
        var location = new Mock<ILocationClient>();
        location.Setup(l => l.GetNearbyDriversAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>()))
            .ReturnsAsync(nearby);

        var identity = new Mock<IActiveDriversService>();
        identity.Setup(i => i.GetActiveDriversAsync(It.IsAny<string?>())).ReturnsAsync(vehicleDrivers);

        var options = Options.Create(new MatchingOptions { RadiusKm = 5, MaxDrivers = maxDrivers });
        return new DriverMatchingService(location.Object, identity.Object, options, NullLogger<DriverMatchingService>.Instance);
    }

    private static NearbyDriverLocation Nearby(string id, double latOffset) =>
        new() { DriverId = id, Lat = PickupLat + latOffset, Lng = PickupLng, Status = "Online" };

    private static ActiveDriver Vehicle(string id, string type = "TUK") =>
        new() { DriverId = id, VehicleTypeCode = type, VehiclePlate = $"PLATE-{id}" };

    private static FindNearbyDriversRequest Request() =>
        new() { PickupLat = PickupLat, PickupLng = PickupLng, VehicleTypeCode = "TUK" };

    [Fact]
    public async Task NoNearbyDrivers_ReturnsNotMatched()
    {
        var service = CreateService(new(), new() { Vehicle("d1") });

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.False(result.Matched);
        Assert.Empty(result.Drivers);
        Assert.Equal(5, result.RadiusKm);
    }

    [Fact]
    public async Task NearbyDriverWithWrongVehicleType_IsExcluded()
    {
        // d1 is nearby and online, but identity-auth doesn't list them for the requested type.
        var service = CreateService(new() { Nearby("d1", 0.001) }, new() { Vehicle("someone-else") });

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.False(result.Matched);
    }

    [Fact]
    public async Task VehicleTypeDriverNotNearby_IsExcluded()
    {
        // identity-auth knows d1 has the right vehicle, but goride-location doesn't report them available nearby.
        var service = CreateService(new(), new() { Vehicle("d1") });

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.False(result.Matched);
    }

    [Fact]
    public async Task MatchedDrivers_AreSortedNearestFirst_WithDistanceAndVehicleInfo()
    {
        var service = CreateService(
            new() { Nearby("far", 0.02), Nearby("near", 0.001), Nearby("mid", 0.01) },
            new() { Vehicle("far"), Vehicle("near"), Vehicle("mid") });

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.True(result.Matched);
        Assert.Equal(new[] { "near", "mid", "far" }, result.Drivers.Select(d => d.DriverId));
        Assert.Equal("PLATE-near", result.Drivers[0].VehiclePlate);
        Assert.InRange(result.Drivers[0].DistanceKm, 0.05, 0.2);   // ~0.11 km
        Assert.InRange(result.Drivers[2].DistanceKm, 2.0, 2.5);    // ~2.22 km
    }

    [Fact]
    public async Task Result_IsCappedAtMaxDrivers_KeepingTheNearest()
    {
        var service = CreateService(
            new() { Nearby("a", 0.03), Nearby("b", 0.01), Nearby("c", 0.02) },
            new() { Vehicle("a"), Vehicle("b"), Vehicle("c") },
            maxDrivers: 2);

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.Equal(new[] { "b", "c" }, result.Drivers.Select(d => d.DriverId));
    }

    [Fact]
    public async Task DuplicateIdentityEntries_DoNotThrow()
    {
        var service = CreateService(new() { Nearby("d1", 0.001) }, new() { Vehicle("d1"), Vehicle("d1") });

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.Single(result.Drivers);
    }
}
