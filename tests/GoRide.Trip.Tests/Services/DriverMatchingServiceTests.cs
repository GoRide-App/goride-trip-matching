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

    [Theory]
    [InlineData(1, "b")]
    [InlineData(2, "b,c")]
    [InlineData(3, "b,c,a")]
    [InlineData(10, "b,c,a")]   // a cap above the number of matches changes nothing
    public async Task Result_IsCappedAtMaxDrivers_KeepingTheNearest(int maxDrivers, string expectedIds)
    {
        // b is nearest, then c, then a.
        var service = CreateService(
            new() { Nearby("a", 0.03), Nearby("b", 0.01), Nearby("c", 0.02) },
            new() { Vehicle("a"), Vehicle("b"), Vehicle("c") },
            maxDrivers);

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.Equal(expectedIds.Split(','), result.Drivers.Select(d => d.DriverId));
    }

    [Fact]
    public async Task DuplicateIdentityEntries_DoNotThrow()
    {
        var service = CreateService(new() { Nearby("d1", 0.001) }, new() { Vehicle("d1"), Vehicle("d1") });

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.Single(result.Drivers);
    }

    // ---- Mutation-targeted tests ----

    // Kills arithmetic mutants inside the private HaversineKm helper:
    //   sin → cos, * → +, 2 * → 0 *, 1 - a → 1 + a, EarthRadiusKm replaced.
    // The three coordinate pairs produce very different arc lengths, so a wrong
    // formula will produce a wrong answer for at least one of them.
    [Theory]
    [InlineData(6.9344, 79.8428, 6.8905, 79.8565, 5.10, 5.12)]  // Colombo Fort driver → Bambalapitiya pickup ≈ 5.11 km
    [InlineData(6.9271, 79.8612, 6.9147, 79.8731, 1.89, 1.92)]  // Town Hall driver → Borella pickup ≈ 1.90 km
    [InlineData(6.0000, 80.0000, 7.0000, 80.0000, 111.0, 111.5)] // 1° latitude ≈ 111.19 km
    public async Task HaversineDistance_IsWithinExpectedRangeForKnownCoordinates(
        double driverLat, double driverLng,
        double pickupLat, double pickupLng,
        double minExpected, double maxExpected)
    {
        var location = new Mock<ILocationClient>();
        location.Setup(l => l.GetNearbyDriversAsync(pickupLat, pickupLng, It.IsAny<double>()))
            .ReturnsAsync(new List<NearbyDriverLocation>
            {
                new() { DriverId = "d1", Lat = driverLat, Lng = driverLng, Status = "Online" },
            });
        var identity = new Mock<IActiveDriversService>();
        identity.Setup(i => i.GetActiveDriversAsync(It.IsAny<string?>())).ReturnsAsync(new List<ActiveDriver>
        {
            new() { DriverId = "d1", VehicleTypeCode = "TUK", VehiclePlate = "PLATE-d1" },
        });
        var svc = new DriverMatchingService(
            location.Object, identity.Object,
            Options.Create(new MatchingOptions { RadiusKm = 200, MaxDrivers = 10 }),
            NullLogger<DriverMatchingService>.Instance);
        var request = new FindNearbyDriversRequest { PickupLat = pickupLat, PickupLng = pickupLng, VehicleTypeCode = "TUK" };

        var result = await svc.FindNearbyDriversAsync(request);

        var distanceKm = result.Drivers.Single().DistanceKm;
        Assert.InRange(distanceKm, minExpected, maxExpected);
    }

    // Kills the `> 0` → `>= 0` mutant on the Matched flag:
    // when there are zero matched drivers the response must be Matched=false, not true.
    [Fact]
    public async Task ZeroMatchedDrivers_MatchedIsFalse_NotTrue()
    {
        // Both services return data but there is no overlap on DriverId —
        // the join produces an empty list, so Matched must be exactly false.
        var service = CreateService(
            new() { Nearby("d1", 0.001) },
            new() { Vehicle("d2") });  // d2 ≠ d1 → no intersection

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.False(result.Matched);   // kills Matched = matched.Count >= 0 mutant
        Assert.Empty(result.Drivers);
    }

    // Kills an OrderBy → OrderByDescending mutation:
    // the nearest driver must appear first, not last.
    [Fact]
    public async Task Drivers_AreOrderedNearestFirst_ClosestIsIndexZero()
    {
        // Three drivers at increasing offsets.  "near" is 0.001°, "mid" 0.01°, "far" 0.02°.
        var service = CreateService(
            new() { Nearby("far", 0.02), Nearby("near", 0.001), Nearby("mid", 0.01) },
            new() { Vehicle("far"), Vehicle("near"), Vehicle("mid") });

        var result = await service.FindNearbyDriversAsync(Request());

        // Ordering is strictly ascending by distance; any reversal fails this.
        var distances = result.Drivers.Select(d => d.DistanceKm).ToList();
        Assert.Equal(distances.OrderBy(x => x).ToList(), distances); // kills OrderByDescending mutant
        Assert.Equal("near", result.Drivers[0].DriverId);            // belt-and-braces: names also match
    }

    // Kills removal or off-by-one of the `.Take(_options.MaxDrivers)` cap:
    // setting MaxDrivers=2 with 3 available must return exactly 2.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(10, 3)] // cap above count returns all
    public async Task MaxDrivers_CapsReturnedDriversToConfiguredLimit(int maxDrivers, int expectedCount)
    {
        var service = CreateService(
            new() { Nearby("a", 0.01), Nearby("b", 0.02), Nearby("c", 0.03) },
            new() { Vehicle("a"), Vehicle("b"), Vehicle("c") },
            maxDrivers);

        var result = await service.FindNearbyDriversAsync(Request());

        Assert.Equal(expectedCount, result.Drivers.Count); // kills Take() removal
    }

    // Kills `Math.Round(..., 2)` → `Math.Round(..., 3)` or outright removal:
    // the returned DistanceKm must be exactly 2 decimal places.
    [Fact]
    public async Task DistanceKm_IsRoundedToExactlyTwoDecimalPlaces()
    {
        // Offset of 0.00123° latitude ≈ 0.137 km — the raw Haversine is many decimal places.
        var service = CreateService(
            new() { Nearby("d1", 0.00123) },
            new() { Vehicle("d1") });

        var result = await service.FindNearbyDriversAsync(Request());

        var d = result.Drivers.Single().DistanceKm;
        // Check the value is rounded to exactly 2 decimal places:
        // multiplying by 100 and truncating must equal the rounded value × 100.
        Assert.Equal(Math.Round(d, 2), d);                           // kills rounding removal
        Assert.Equal(d, Math.Round(d * 100) / 100);                  // kills Round(...,3) mutant
    }
}
