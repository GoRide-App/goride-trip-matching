using Moq;
using Xunit;
using GoRide.Trip.Data;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

namespace GoRide.Trip.Tests.Services;

public class FareCalculationServiceTests
{
    // Colombo Fort -> Bambalapitiya, roughly 5 km apart in a straight line.
    private const decimal StartLat = 6.9344m;
    private const decimal StartLng = 79.8428m;
    private const decimal EndLat = 6.8905m;
    private const decimal EndLng = 79.8565m;

    // A null route means goride-location is unreachable, so the service falls back to straight-line distance.
    private static Mock<ILocationClient> LocationClient((double DistanceKm, double DurationMinutes)? route)
    {
        var location = new Mock<ILocationClient>();
        location.Setup(l => l.GetRoutePlanAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>()))
            .ReturnsAsync(route);
        return location;
    }

    private static Mock<IVehicleTypeRepository> Repository(params VehicleType[] vehicleTypes)
    {
        var repository = new Mock<IVehicleTypeRepository>();
        repository.Setup(r => r.GetAllAsync()).ReturnsAsync(vehicleTypes.ToList());
        return repository;
    }

    private static VehicleType Vehicle(string code, bool active = true) =>
        new() { Id = $"vt_{code.ToLowerInvariant()}", Code = code, BaseFare = 100m, RatePerKm = 40m, RatePerMin = 5m, Active = active };

    // The repo's tuk-tuk is returned alongside the display-only CAR/BIKE/XL options the service adds.
    private static FareOption TukTuk(List<FareOption> options) => options.Single(o => o.VehicleTypeCode == "TUKTUK");

    private static Task<List<FareOption>> Estimate(
        Mock<IVehicleTypeRepository> repository,
        Mock<ILocationClient> location,
        decimal endLat = EndLat,
        decimal endLng = EndLng) =>
        new FareCalculationService(repository.Object, location.Object)
            .EstimateFaresAsync(StartLat, StartLng, endLat, endLng);

    [Theory]
    [InlineData("TUKTUK", true, true)]
    [InlineData("TUK", true, true)]       // the short code identity-auth uses
    [InlineData("tuktuk", true, true)]    // matching is case-insensitive
    [InlineData("TUKTUK", false, false)]  // an inactive tuk-tuk can't be picked
    [InlineData("CAR", true, false)]      // only tuk-tuks are bookable at this stage
    [InlineData("BIKE", true, false)]
    [InlineData("XL", true, false)]
    public async Task VehicleTypeAvailability_OnlyActiveTukTuksAreBookable(string code, bool active, bool expectedAvailable)
    {
        var result = await Estimate(Repository(Vehicle(code, active)), LocationClient(null));

        Assert.Equal(expectedAvailable, result.Single(o => o.VehicleTypeCode == code).Available);
    }

    [Theory]
    [InlineData("CAR", 1000, "vt_car")]   // 300 + 5 km x 120 + 10 min x 10
    [InlineData("BIKE", 400, "vt_bike")]   // 100 + 5 km x 50  + 10 min x 5
    [InlineData("XL", 1550, "vt_xl")]    // 500 + 5 km x 180 + 10 min x 15
    public async Task VehicleTypesMissingFromTheRepo_AreAddedAsDisplayOnlyWithTheirOwnRates(string code, double expectedFare, string expectedId)
    {
        var result = await Estimate(Repository(), LocationClient((DistanceKm: 5, DurationMinutes: 10)));

        var option = result.Single(o => o.VehicleTypeCode == code);
        Assert.False(option.Available);
        Assert.Equal((decimal)expectedFare, option.Fare);
        // Explicitly assert metadata to kill string/value mutants in fallback definitions
        Assert.Equal(expectedId, option.VehicleTypeId);
        Assert.Equal(5.0, option.DistanceKm);
        Assert.Equal(10.0, option.EstimatedDurationMinutes);
    }

    [Theory]
    [InlineData(0, 0, 100)]            // base fare only
    [InlineData(5, 10, 350)]           // 100 + 5 x 40 + 10 x 5
    [InlineData(12.5, 30, 750)]        // 100 + 12.5 x 40 + 30 x 5
    [InlineData(1.234, 2.5, 161.86)]   // fractions are rounded to the cent
    public async Task Fare_IsBaseFarePlusDistanceAndTimeCharges_UsingTheRoutedValues(double distanceKm, double durationMinutes, double expectedFare)
    {
        var location = LocationClient((distanceKm, durationMinutes));

        var result = await Estimate(Repository(Vehicle("TUKTUK")), location);

        Assert.Equal((decimal)expectedFare, TukTuk(result).Fare);
        location.Verify(l => l.GetRoutePlanAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>()), Times.Once);
    }

    [Theory]
    [InlineData(3.14159, 7.26, 3.14, 7.3)]
    [InlineData(10.999, 59.94, 11.0, 59.9)]
    [InlineData(0.4, 0.04, 0.4, 0.0)]
    public async Task DistanceAndDuration_AreRoundedForDisplay(double distanceKm, double durationMinutes, double expectedDistance, double expectedDuration)
    {
        var result = await Estimate(Repository(Vehicle("TUKTUK")), LocationClient((distanceKm, durationMinutes)));

        var option = TukTuk(result);
        Assert.Equal(expectedDistance, option.DistanceKm);
        Assert.Equal(expectedDuration, option.EstimatedDurationMinutes);
    }

    [Theory]
    [InlineData("TUKTUK", "Tuk Tuk")]
    [InlineData("TUK", "Tuk Tuk")]
    [InlineData("CAR", "Car")]
    [InlineData("BIKE", "Bike")]
    [InlineData("XL", "XL")]
    [InlineData("MINIBUS", "MINIBUS")]   // an unknown code is shown as-is rather than failing
    public async Task DisplayName_IsFriendlyForKnownCodes_AndTheCodeItselfOtherwise(string code, string expectedName)
    {
        var result = await Estimate(Repository(Vehicle(code)), LocationClient(null));

        Assert.Equal(expectedName, result.Single(o => o.VehicleTypeCode == code).DisplayName);
    }

    [Fact]
    public async Task WhenLocationIsUnavailable_DistanceFallsBackToTheStraightLine()
    {
        var result = await Estimate(Repository(Vehicle("TUKTUK")), LocationClient(null));

        var tukTuk = TukTuk(result);
        // Colombo Fort (6.9344, 79.8428) -> Bambalapitiya (6.8905, 79.8565)
        // Exact Haversine distance is 5.11 km; at 25 km/h fallback speed: (5.11 / 25) * 60 = 12.3 minutes.
        // Asserting exact rounded results kills arithmetic mutators (+ to -, * to /, 1-a to 1+a)
        Assert.Equal(5.11, tukTuk.DistanceKm);
        Assert.Equal(12.3, tukTuk.EstimatedDurationMinutes);
    }

    [Theory]
    [InlineData(6.9344, 79.8428, 6.8905, 79.8565, 5.11)]   // Colombo Fort to Bambalapitiya
    [InlineData(6.9271, 79.8612, 6.9147, 79.8731, 1.90)]   // Town Hall to Borella
    [InlineData(6.0000, 80.0000, 7.0000, 80.0000, 111.19)] // 1 degree latitude difference (~111.19 km)
    public async Task FallbackHaversineFormula_CalculatesPreciseGreatCircleDistance(
        double lat1, double lng1, double lat2, double lng2, double expectedKm)
    {
        // Parameterized precision test specifically designed to kill arithmetic formula mutants
        var result = await new FareCalculationService(Repository(Vehicle("TUKTUK")).Object, LocationClient(null).Object)
            .EstimateFaresAsync((decimal)lat1, (decimal)lng1, (decimal)lat2, (decimal)lng2);

        var tukTuk = TukTuk(result);
        Assert.Equal(expectedKm, tukTuk.DistanceKm);
    }

    [Fact]
    public async Task SamePickupAndDropoff_ChargesTheBaseFareOnly()
    {
        var result = await Estimate(Repository(Vehicle("TUKTUK")), LocationClient(null), endLat: StartLat, endLng: StartLng);

        var tukTuk = TukTuk(result);
        Assert.Equal(0, tukTuk.DistanceKm);
        Assert.Equal(100m, tukTuk.Fare);
    }
}
