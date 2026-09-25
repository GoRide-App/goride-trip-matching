using GoRide.Trip.Data;
using GoRide.Trip.Events;
using GoRide.Trip.Models;
using Microsoft.Extensions.Options;

namespace GoRide.Trip.Services;

public interface IRideRequestService
{
    /// <returns>The drivers the request was offered to, or an Error when the trip already has an accepted driver.</returns>
    Task<(FindNearbyDriversResponse? Result, string? Error)> DeliverAsync(FindNearbyDriversRequest request);

    /// <summary>Where a delivered request stands, or null when nothing was ever offered for this trip.</summary>
    Task<RideRequestStatus?> GetStatusAsync(string tripId);
}

/// <summary>
/// Finds nearby available drivers (see DriverMatchingService), records a Pending
/// offer for each, and publishes a RIDE_REQUESTED event per driver to Kafka —
/// goride-notification's consumer turns those into push notifications.
/// The offer row is what makes a request reach a driver (their app polls for it);
/// the Kafka event is an extra push on top, so failing to publish it is logged
/// but does not fail the request.
/// </summary>
public class RideRequestService : IRideRequestService
{
    private readonly IDriverMatchingService _matching;
    private readonly IDriverOfferRepository _offers;
    private readonly ITripEventPublisher _publisher;
    private readonly IActiveDriversService _activeDrivers;
    private readonly MatchingOptions _options;
    private readonly ILogger<RideRequestService> _logger;

    public RideRequestService(
        IDriverMatchingService matching,
        IDriverOfferRepository offers,
        ITripEventPublisher publisher,
        IActiveDriversService activeDrivers,
        IOptions<MatchingOptions> options,
        ILogger<RideRequestService> logger)
    {
        _matching = matching;
        _offers = offers;
        _publisher = publisher;
        _activeDrivers = activeDrivers;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(FindNearbyDriversResponse? Result, string? Error)> DeliverAsync(FindNearbyDriversRequest request)
    {
        var tripId = request.TripId!;
        var riderId = request.RiderId!;

        if (await _offers.HasAcceptedOfferAsync(tripId))
            return (null, "This trip already has an accepted driver.");

        var search = await _matching.FindNearbyDriversAsync(request);
        if (!search.Matched)
            return (search, null);

        await Task.WhenAll(search.Drivers.Select(d => OfferToDriverAsync(request, tripId, riderId, d)));

        _logger.LogInformation("Offered ride request for trip {TripId} to {Count} driver(s).", tripId, search.Drivers.Count);

        return (search, null);
    }

    private static readonly HashSet<string> WonStatuses = new() { "Accepted", "Arrived", "InProgress", "Completed" };

    public async Task<RideRequestStatus?> GetStatusAsync(string tripId)
    {
        var offers = await _offers.GetOffersForTripAsync(tripId);
        if (offers.Count == 0) return null;

        var won = offers.FirstOrDefault(o => WonStatuses.Contains(o.Status));
        if (won is not null)
        {
            return new RideRequestStatus
            {
                TripId = tripId,
                Status = won.Status,
                // Vehicle details come from identity-auth; if that's down the rider still learns a driver accepted.
                Driver = await FindDriverAsync(won.DriverId) ?? new ActiveDriver { DriverId = won.DriverId },
                PickupLocation = won.PickupLocation,
                PickupLat = won.PickupLat,
                PickupLng = won.PickupLng,
                DropoffLocation = won.DropoffLocation,
                DropoffLat = won.DropoffLat,
                DropoffLng = won.DropoffLng,
                Fare = won.Fare,
            };
        }

        var now = DateTime.UtcNow;
        var stillOpen = offers.Any(o => o.Status == "Pending" && o.CreatedAt.AddSeconds(_options.OfferTtlSeconds) > now);
        return new RideRequestStatus { TripId = tripId, Status = stillOpen ? "Searching" : "NoDriver" };
    }

    private async Task<ActiveDriver?> FindDriverAsync(string driverId)
    {
        try
        {
            var drivers = await _activeDrivers.GetActiveDriversAsync(null);
            return drivers.FirstOrDefault(d => d.DriverId == driverId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look up vehicle details for driver {DriverId}.", driverId);
            return null;
        }
    }

    private async Task OfferToDriverAsync(FindNearbyDriversRequest request, string tripId, string riderId, MatchedDriver driver)
    {
        // The offer is what a driver's app sees, so it is recorded first. If that fails the whole
        // request fails, because nobody could ever act on it.
        await _offers.CreatePendingAsync(tripId, driver.DriverId, riderId, driver.DistanceKm,
            request.PickupLocation, request.PickupLat, request.PickupLng,
            request.DropoffLocation, request.DropoffLat, request.DropoffLng, request.Fare);

        try
        {
            await _publisher.PublishAsync(new TripEvent
            {
                EventType = "RIDE_REQUESTED",
                TripId = tripId,
                RiderId = riderId,
                DriverId = driver.DriverId,
                Payload = new TripEventPayload
                {
                    PickupLocation = request.PickupLocation,
                    DropoffLocation = request.DropoffLocation,
                    Fare = request.Fare,
                    VehicleType = driver.VehicleTypeCode,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish RIDE_REQUESTED for trip {TripId} to driver {DriverId}; the driver can still see the offer in the app.",
                tripId, driver.DriverId);
        }
    }
}
