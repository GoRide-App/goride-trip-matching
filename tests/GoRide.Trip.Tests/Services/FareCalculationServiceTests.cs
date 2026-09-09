using Moq;
using Xunit;
using GoRide.Trip.Data;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

namespace GoRide.Trip.Tests.Services;

public class FareCalculationServiceTests {
    [Fact]
    public async Task EstimateFaresAsync_TukTukVehicleType_IsAvailable() {  // Test for TukTuk vehicle type availability
        // Arrange
        var mockRepo = new Mock<IVehicleTypeRepository>();  // Create a mock repository for vehicle types
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<VehicleType> {  // Setup the mock to return a list containing a TukTuk vehicle type
            new VehicleType { Id = "vt_tuktuk", Code = "TUKTUK", BaseFare = 100m, RatePerKm = 40m, RatePerMin = 5m, Active = true }  
        });

        var service = new FareCalculationService(mockRepo.Object);  // Create an instance of the FareCalculationService using the mock repository

        // Act — Colombo Fort to Bambalapitiya, roughly
        var result = await service.EstimateFaresAsync(6.9344m, 79.8428m, 6.8905m, 79.8565m);   // Call the EstimateFaresAsync method with coordinates for Colombo Fort to Bambalapitiya

        // Assert
        var tuktuk = result.Single(o => o.VehicleTypeCode == "TUKTUK");  // Get the fare estimate for the TukTuk vehicle type from the result
        Assert.True(tuktuk.Available);   // Assert that the TukTuk vehicle type is available
        Assert.True(tuktuk.DistanceKm > 0);   // Assert that the distance in kilometers is greater than 0
    }

    [Fact]
    public async Task EstimateFaresAsync_NonTukTukFromRepo_IsNotAvailable() {  // Test for non-TukTuk vehicle type availability
        var mockRepo = new Mock<IVehicleTypeRepository>();   // Create a mock repository for vehicle types
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<VehicleType> {
            new VehicleType { Id = "vt_car", Code = "CAR", BaseFare = 300m, RatePerKm = 120m, RatePerMin = 10m, Active = true }
        });    // Setup the mock to return a list containing a CAR vehicle type

        var service = new FareCalculationService(mockRepo.Object);  // Create an instance of the FareCalculationService using the mock repository

        var result = await service.EstimateFaresAsync(6.9344m, 79.8428m, 6.8905m, 79.8565m);  // Call the EstimateFaresAsync method with coordinates for Colombo Fort to Bambalapitiya

        Assert.False(result.Single(o => o.VehicleTypeCode == "CAR").Available);   // Assert that the CAR vehicle type is not available
    }

    [Fact]
    public async Task EstimateFaresAsync_RepoMissingBikeAndXl_AddsThemAsDisplayOnly() {    // Test for missing vehicle types in the repository
        var mockRepo = new Mock<IVehicleTypeRepository>();    // Create a mock repository for vehicle types
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<VehicleType>());   // Setup the mock to return an empty list, simulating a repository with no vehicle types

        var service = new FareCalculationService(mockRepo.Object);  // Create an instance of the FareCalculationService using the mock repository

        var result = await service.EstimateFaresAsync(6.9344m, 79.8428m, 6.8905m, 79.8565m);   // Call the EstimateFaresAsync method with coordinates for Colombo Fort to Bambalapitiya

        Assert.Contains(result, o => o.VehicleTypeCode == "BIKE" && !o.Available);   // Assert that the result contains a BIKE vehicle type that is not available
        Assert.Contains(result, o => o.VehicleTypeCode == "XL" && !o.Available);   // Assert that the result contains an XL vehicle type that is not available
    }

    [Fact]

    public async Task EstimateFaresAsync_ZeroDistance_ReturnsBaseFareOnly() {
        var mockupRepo = new Mock<IVehicleTypeRepository>();
        mockupRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<VehicleType> {
            new VehicleType {Id = "vt_tuktuk", Code = "TUKTUK", BaseFare = 100m, RatePerKm = 40m, RatePerMin = 5m, Active = true }
        });

        var service = new FareCalculationService(mockupRepo.Object);

        var result = await service.EstimateFaresAsync(6.9344m, 79.8428m, 6.9344m, 79.8428m); // Same start and end coordinates

        Assert.Single(result, o => o.VehicleTypeCode == "TUKTUK" && o.DistanceKm == 0 && o.Fare == 100m); // Assert that the estimated fare is equal to the base fare when distance is zero

    }
}