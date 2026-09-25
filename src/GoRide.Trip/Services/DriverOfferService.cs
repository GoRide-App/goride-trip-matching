using GoRide.Trip.Data;
using GoRide.Trip.Events;
using GoRide.Trip.Models;
using Microsoft.Extensions.Options;

namespace GoRide.Trip.Services;

public enum AcceptOutcome
{
    Accepted,

    /// <summary>This driver was never offered this trip.</summary>
    NotFound,

    /// <summary>The offer exists but can no longer be accepted (expired, already decided, or failed).</summary>
    NotAvailable,
}

public enum StatusUpdateOutcome
{
    Updated,

    /// <summary>This driver has no (won) offer for this trip at all.</summary>
    NotFound,

    /// <summary>The offer exists but isn't in the right status for this action (e.g. already started, or not yet accepted).</summary>
    InvalidTransition,
}

public interface IDriverOfferService
{
    /// <summary>The driver's pending, not-yet-expired offers, newest first.</summary>
    Task<List<DriverOffer>> GetPendingAsync(string driverId);

    Task<(AcceptOutcome Outcome, DriverOffer? Offer)> AcceptAsync(string tripId, string driverId);

    /// <summary>
    /// Advances the trip through its post-acceptance stages one at a time: Accepted -> Arrived ->
    /// InProgress -> Completed. Each call must name the very next stage; skipping or going backwards
    /// is rejected as InvalidTransition.
    /// </summary>
    Task<(StatusUpdateOutcome Outcome, DriverOffer? Offer)> UpdateStatusAsync(string tripId, string driverId, string action);
}

/// <summary>
/// The driver's side of a ride request: see the offers waiting for them, and accept one.
/// </summary>
public class DriverOfferService : IDriverOfferService
{
    private readonly IDriverOfferRepository _offers;
    private readonly ITripEventPublisher _publisher;
    private readonly IActiveDriversService _activeDrivers;
    private readonly MatchingOptions _options;
    private readonly ILogger<DriverOfferService> _logger;

    public DriverOfferService(
        IDriverOfferRepository offers,
        ITripEventPublisher publisher,
        IActiveDriversService activeDrivers,
        IOptions<MatchingOptions> options,
        ILogger<DriverOfferService> logger)
    {
        _offers = offers;
        _publisher = publisher;
        _activeDrivers = activeDrivers;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<DriverOffer>> GetPendingAsync(string driverId)
    {
        var ttl = _options.OfferTtlSeconds;
        var offers = await _offers.GetPendingForDriverAsync(driverId, ttl);
        foreach (var offer in offers)
            offer.ExpiresAt = offer.CreatedAt.AddSeconds(ttl);
        return offers;
    }

    public async Task<(AcceptOutcome Outcome, DriverOffer? Offer)> AcceptAsync(string tripId, string driverId)
    {
        var accepted = await _offers.TryAcceptAsync(tripId, driverId, _options.OfferTtlSeconds);
        if (accepted is null)
        {
            // Only reached on failure, so the extra lookup costs nothing on the happy path.
            var status = await _offers.GetStatusAsync(tripId, driverId);
            return status is null ? (AcceptOutcome.NotFound, null) : (AcceptOutcome.NotAvailable, null);
        }

        accepted.ExpiresAt = accepted.CreatedAt.AddSeconds(_options.OfferTtlSeconds);
        await PublishDriverAcceptedAsync(accepted);
        return (AcceptOutcome.Accepted, accepted);
    }

    private static readonly Dictionary<string, string> RequiredPreviousStatus = new()
    {
        ["Arrived"] = "Accepted",
        ["InProgress"] = "Arrived",
        ["Completed"] = "InProgress",
    };

    public async Task<(StatusUpdateOutcome Outcome, DriverOffer? Offer)> UpdateStatusAsync(string tripId, string driverId, string action)
    {
        if (!RequiredPreviousStatus.TryGetValue(action, out var fromStatus))
            return (StatusUpdateOutcome.InvalidTransition, null);

        var updated = await _offers.TryAdvanceStatusAsync(tripId, driverId, fromStatus, action);
        if (updated is not null) return (StatusUpdateOutcome.Updated, updated);

        var status = await _offers.GetStatusAsync(tripId, driverId);
        return status is null ? (StatusUpdateOutcome.NotFound, null) : (StatusUpdateOutcome.InvalidTransition, null);
    }

    // The accept is already committed by the time we get here, and retrying it would
    // fail (the offer is no longer Pending) — so a failure to notify the rider is
    // logged rather than turned into a failed accept.
    private async Task PublishDriverAcceptedAsync(DriverOffer offer)
    {
        try
        {
            var vehicle = await FindVehicleAsync(offer.DriverId);

            await _publisher.PublishAsync(new TripEvent
            {
                EventType = "DRIVER_ACCEPTED",
                TripId = offer.TripId,
                RiderId = offer.RiderId,
                DriverId = offer.DriverId,
                Payload = new TripEventPayload
                {
                    VehiclePlate = vehicle?.VehiclePlate,
                    VehicleType = vehicle?.VehicleTypeCode,
                    Fare = offer.Fare,
                    PickupLocation = offer.PickupLocation,
                    DropoffLocation = offer.DropoffLocation,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trip {TripId} was accepted by driver {DriverId} but the DRIVER_ACCEPTED event could not be published.",
                offer.TripId, offer.DriverId);
        }
    }

    // Vehicle details only enrich the rider's notification, so a failed lookup just leaves them blank.
    private async Task<ActiveDriver?> FindVehicleAsync(string driverId)
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
}
