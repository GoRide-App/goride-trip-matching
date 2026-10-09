using GoRide.Trip.Models;
using Microsoft.Extensions.Caching.Memory;

namespace GoRide.Trip.Services;

public interface IDriverProfileLookup
{
    /// <summary>
    /// The driver's vehicle details from identity-auth, or null when identity-auth doesn't list
    /// them or can't be reached. Never throws -- the details only enrich a response or an event.
    /// </summary>
    Task<ActiveDriver?> FindAsync(string driverId);
}

/// <summary>
/// Vehicle details for one driver, for the DRIVER_ACCEPTED event and the rider's status poll.
/// identity-auth can only list every active driver at once, and the rider polls every few
/// seconds, so the list is cached per driver rather than refetched on each accept and poll.
/// </summary>
public class DriverProfileLookup : IDriverProfileLookup
{
    // Vehicle details don't change mid-trip, so a minute-old copy is fine. A driver identity-auth
    // didn't list (or a failed call) is only remembered briefly: long enough that polls don't hammer
    // an identity-auth that's down, short enough that a driver who just came online soon shows up.
    private static readonly TimeSpan FoundFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MissingFor = TimeSpan.FromSeconds(10);

    private readonly IActiveDriversService _activeDrivers;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DriverProfileLookup> _logger;

    public DriverProfileLookup(IActiveDriversService activeDrivers, IMemoryCache cache, ILogger<DriverProfileLookup> logger)
    {
        _activeDrivers = activeDrivers;
        _cache = cache;
        _logger = logger;
    }

    private static string Key(string driverId) => $"identity-auth:driver-profile:{driverId}";

    public async Task<ActiveDriver?> FindAsync(string driverId)
    {
        // A cached null is a recent miss, and counts as a hit.
        if (_cache.TryGetValue(Key(driverId), out ActiveDriver? cached))
            return cached;

        ActiveDriver? found = null;
        try
        {
            // One call returns every active driver, so remember them all, not just this one.
            var drivers = await _activeDrivers.GetActiveDriversAsync(null);
            foreach (var driver in drivers.DistinctBy(d => d.DriverId))
                _cache.Set(Key(driver.DriverId), driver, FoundFor);
            found = drivers.FirstOrDefault(d => d.DriverId == driverId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look up vehicle details for driver {DriverId}.", driverId);
        }

        if (found is null)
            _cache.Set<ActiveDriver?>(Key(driverId), null, MissingFor);
        return found;
    }
}
