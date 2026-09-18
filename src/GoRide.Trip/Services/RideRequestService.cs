using GoRide.Trip.Data;
using GoRide.Trip.Events;
using GoRide.Trip.Models;

namespace GoRide.Trip.Services;

/// <summary>Matches were found but the request couldn't be delivered to any of them.</summary>
public class DeliveryFailedException : Exception
{
    public DeliveryFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IRideRequestService
{
    /// <returns>The drivers the request was delivered to, or an Error when the trip already has an accepted driver.</returns>
    Task<(FindNearbyDriversResponse? Result, string? Error)> DeliverAsync(FindNearbyDriversRequest request);
}

/// <summary>
/// Finds nearby available drivers (see DriverMatchingService), records a Pending
/// offer for each, and publishes a RIDE_REQUESTED event per driver to Kafka —
/// goride-notification's consumer turns those into push notifications.
/// </summary>
public class RideRequestService : IRideRequestService
{
    private readonly IDriverMatchingService _matching;
    private readonly IDriverOfferRepository _offers;
    private readonly ITripEventPublisher _publisher;
    private readonly ILogger<RideRequestService> _logger;

    public RideRequestService(
        IDriverMatchingService matching,
        IDriverOfferRepository offers,
        ITripEventPublisher publisher,
        ILogger<RideRequestService> logger)
    {
        _matching = matching;
        _offers = offers;
        _publisher = publisher;
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

        var delivered = new List<MatchedDriver>();
        var outcomes = await Task.WhenAll(search.Drivers.Select(d => DeliverToDriverAsync(request, tripId, riderId, d)));
        for (var i = 0; i < outcomes.Length; i++)
            if (outcomes[i]) delivered.Add(search.Drivers[i]);

        if (delivered.Count == 0)
            throw new DeliveryFailedException($"Found {search.Drivers.Count} driver(s) for trip {tripId} but could not deliver the request to any of them.");

        _logger.LogInformation("Delivered ride request for trip {TripId} to {Delivered}/{Matched} driver(s).",
            tripId, delivered.Count, search.Drivers.Count);

        return (new FindNearbyDriversResponse { Matched = true, RadiusKm = search.RadiusKm, Drivers = delivered }, null);
    }

    private async Task<bool> DeliverToDriverAsync(FindNearbyDriversRequest request, string tripId, string riderId, MatchedDriver driver)
    {
        // Record the offer BEFORE publishing, so a driver who acts on the
        // notification immediately can never beat the row into existence.
        await _offers.CreatePendingAsync(tripId, driver.DriverId, riderId, driver.DistanceKm);

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
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish RIDE_REQUESTED for trip {TripId} to driver {DriverId}.", tripId, driver.DriverId);
            try { await _offers.MarkFailedAsync(tripId, driver.DriverId); }
            catch (Exception markEx) { _logger.LogWarning(markEx, "Could not mark offer failed for trip {TripId}, driver {DriverId}.", tripId, driver.DriverId); }
            return false;
        }
    }
}
