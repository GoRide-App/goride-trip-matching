using System.Globalization;
using System.Net.Http.Json;
using GoRide.Trip.Models;

namespace GoRide.Trip.Services;

public interface ILocationClient
{
    Task<(double DistanceKm, double DurationMinutes)?> GetRoutePlanAsync(
        double pickupLat, double pickupLng, double destinationLat, double destinationLng);

    /// <summary>Drivers currently marked Online (available) within radiusKm of (lat, lng). Empty list on any failure.</summary>
    Task<List<NearbyDriverLocation>> GetNearbyDriversAsync(double lat, double lng, double radiusKm);
}

/// <summary>
/// Calls goride-location's POST /rides/plan for real road-network distance/duration
/// (ORS, falling back to OSRM, falling back to a geodesic estimate -- all handled
/// inside goride-location itself). Returns null on any failure so the caller can
/// fall back to its own straight-line estimate rather than failing the whole fare
/// estimate just because goride-location is unreachable.
/// </summary>
public class LocationClient : ILocationClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocationClient> _logger;

    public LocationClient(HttpClient httpClient, ILogger<LocationClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<(double DistanceKm, double DurationMinutes)?> GetRoutePlanAsync(
        double pickupLat, double pickupLng, double destinationLat, double destinationLng)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/rides/plan", new
            {
                pickupLat,
                pickupLng,
                destinationLat,
                destinationLng,
            });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "goride-location returned HTTP {StatusCode} for /rides/plan -- falling back to straight-line distance.",
                    (int)response.StatusCode);
                return null;
            }

            var plan = await response.Content.ReadFromJsonAsync<RidePlanResult>();
            if (plan is null) return null;

            return (plan.DistanceKm, plan.DurationMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach goride-location for /rides/plan -- falling back to straight-line distance.");
            return null;
        }
    }

    public async Task<List<NearbyDriverLocation>> GetNearbyDriversAsync(double lat, double lng, double radiusKm)
    {
        try
        {
            var url = $"/location/nearby-drivers?lat={lat.ToString(CultureInfo.InvariantCulture)}" +
                      $"&lng={lng.ToString(CultureInfo.InvariantCulture)}" +
                      $"&radiusKm={radiusKm.ToString(CultureInfo.InvariantCulture)}";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "goride-location returned HTTP {StatusCode} for /location/nearby-drivers -- treating as no nearby drivers.",
                    (int)response.StatusCode);
                return new List<NearbyDriverLocation>();
            }

            var drivers = await response.Content.ReadFromJsonAsync<List<NearbyDriverLocation>>();
            return drivers ?? new List<NearbyDriverLocation>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach goride-location for /location/nearby-drivers -- treating as no nearby drivers.");
            return new List<NearbyDriverLocation>();
        }
    }

    private sealed record RidePlanResult(double DistanceKm, double DurationMinutes);
}
