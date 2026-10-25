using GoRide.Trip.Models;
using Microsoft.Extensions.Options;

namespace GoRide.Trip.Services;

public interface IDriverMatchingService
{
    Task<FindNearbyDriversResponse> FindNearbyDriversAsync(FindNearbyDriversRequest request);  // Task is used for asynchronous operations, allowing the method to run without blocking the calling thread.
}

/// <summary>
/// Finds available drivers of the requested vehicle type near a pickup point.
/// A driver only qualifies if BOTH hold: goride-location reports them Online
/// (available) within the fixed radius, AND identity-auth says their vehicle
/// matches the requested type. Each service is the source of truth for one
/// of those facts, so neither alone is enough.
/// </summary>
public class DriverMatchingService : IDriverMatchingService
{
    // The DriverMatchingService class implements the IDriverMatchingService interface and provides functionality to find nearby drivers based on a pickup location and vehicle type. It uses two services: ILocationClient to get nearby drivers and IActiveDriversService to get active drivers of a specific vehicle type. The service also uses configuration options for matching and logging for monitoring the process.
    private readonly ILocationClient _locationClient;   // The ILocationClient is used to interact with the location service to find nearby drivers based on geographic coordinates. 
    private readonly IActiveDriversService _activeDriversService;   // The IActiveDriversService is used to retrieve information about active drivers, including their vehicle types and other relevant details.
    private readonly MatchingOptions _options;   // The MatchingOptions holds configuration settings for the driver matching process, such as the search radius and maximum number of drivers to return.
    private readonly ILogger<DriverMatchingService> _logger;  // The ILogger is used for logging information, warnings, and errors during the execution of the driver matching process, which helps in monitoring and debugging.

    public DriverMatchingService(  // The constructor of the DriverMatchingService class initializes the service with the required dependencies: ILocationClient, IActiveDriversService, IOptions<MatchingOptions>, and ILogger<DriverMatchingService>. It assigns these dependencies to private readonly fields for use in the service's methods.
        ILocationClient locationClient,  /// The ILocationClient is injected into the service to allow it to call methods for retrieving nearby drivers based on geographic coordinates.
     // ILocationClient locationClient
// Why?
// Because the service only cares about the contract.
// It doesn't care which implementation is used.
// For example:
// GoogleLocationClient
// or
// AzureLocationClient
// or
// FakeLocationClient
// could all implement:
// ILocationClient
// and the code would still work.
        IActiveDriversService activeDriversService,  // The IActiveDriversService is injected to enable the service to fetch active drivers and their vehicle information, which is necessary for filtering drivers based on the requested vehicle type.
        IOptions<MatchingOptions> options,  // The IOptions<MatchingOptions> is injected to provide access to configuration settings that control the behavior of the driver matching process, such as the search radius and maximum number of drivers to return.
        ILogger<DriverMatchingService> logger)
    {
        _locationClient = locationClient;
        _activeDriversService = activeDriversService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<FindNearbyDriversResponse> FindNearbyDriversAsync(FindNearbyDriversRequest request)
    {
        var radiusKm = _options.RadiusKm;

        var nearbyTask = _locationClient.GetNearbyDriversAsync(request.PickupLat, request.PickupLng, radiusKm);
        var vehicleDriversTask = _activeDriversService.GetActiveDriversAsync(request.VehicleTypeCode);
        await Task.WhenAll(nearbyTask, vehicleDriversTask);

        var vehicleDriversById = vehicleDriversTask.Result
            .GroupBy(d => d.DriverId)
            .ToDictionary(g => g.Key, g => g.First());

        // goride-location's response carries no distance, so compute it here for ordering/display.
        var matched = nearbyTask.Result
            .Where(n => vehicleDriversById.ContainsKey(n.DriverId))
            .Select(n =>
            {
                var profile = vehicleDriversById[n.DriverId];
                return new MatchedDriver
                {
                    DriverId = n.DriverId,
                    VehicleTypeCode = profile.VehicleTypeCode,
                    VehicleMake = profile.VehicleMake,
                    VehicleModel = profile.VehicleModel,
                    VehiclePlate = profile.VehiclePlate,
                    Lat = n.Lat,
                    Lng = n.Lng,
                    DistanceKm = Math.Round(HaversineKm(request.PickupLat, request.PickupLng, n.Lat, n.Lng), 2),
                };
            })
            .OrderBy(d => d.DistanceKm)
            .Take(_options.MaxDrivers)
            .ToList();

        _logger.LogInformation(
            "Matched {Count} available {VehicleType} driver(s) within {RadiusKm}km of ({Lat},{Lng}).",
            matched.Count, request.VehicleTypeCode, radiusKm, request.PickupLat, request.PickupLng);

        return new FindNearbyDriversResponse
        {
            Matched = matched.Count > 0,
            RadiusKm = radiusKm,
            Drivers = matched,
        };
    }

    private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadiusKm = 6371.0;
        double dLat = ToRadians(lat2 - lat1);
        double dLng = ToRadians(lng2 - lng1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
