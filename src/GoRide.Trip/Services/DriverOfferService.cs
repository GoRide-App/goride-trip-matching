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

    /// <summary>This driver is already on another trip (Accepted, Arrived or InProgress).</summary>
    DriverBusy,
}

public enum StatusUpdateOutcome
{
    Updated,

    /// <summary>A trip or driver identifier is missing or exceeds the database limit.</summary>
    InvalidRequest,

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
    /// InProgress -> Completed. The current stage is an idempotent no-op; skipping or going backwards
    /// is rejected as InvalidTransition.
    /// </summary>
    Task<(StatusUpdateOutcome Outcome, DriverOffer? Offer)> UpdateStatusAsync(string tripId, string driverId, string action);

    /// <summary>
    /// The trip the driver's app should pick back up after a reload: one still underway (Accepted, Arrived
    /// or InProgress), or else one Completed within the last few hours for compatibility. Completion never blocks availability.
    /// Null when there is none.
    /// </summary>
    Task<DriverOffer?> GetCurrentTripAsync(string driverId);
    Task<(StatusUpdateOutcome Outcome, DriverOffer? Offer)> DecideAsync(string tripId, string driverId, bool cancel);
}

/// <summary>
/// The driver's side of a ride request: see the offers waiting for them, and accept one.
/// </summary>
public class DriverOfferService : IDriverOfferService
{
    private readonly IDriverOfferRepository _offers;
    private readonly ITripEventPublisher _publisher;
    private readonly IDriverProfileLookup _driverProfiles;
    private readonly ITripCompletionNotifier _completionNotifier;
    private readonly MatchingOptions _options;
    private readonly ILogger<DriverOfferService> _logger;

    public DriverOfferService(
        IDriverOfferRepository offers,
        ITripEventPublisher publisher,
        IDriverProfileLookup driverProfiles,
        ITripCompletionNotifier completionNotifier,
        IOptions<MatchingOptions> options,
        ILogger<DriverOfferService> logger)
    {
        _offers = offers;
        _publisher = publisher;
        _driverProfiles = driverProfiles;
        _completionNotifier = completionNotifier;
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
        // A driver works one trip at a time. Checked before the claim so a busy driver never takes a trip
        // away from the other drivers it was offered to. (This trip itself is excluded, so retrying an
        // accept that already went through still gets the usual "no longer available" answer.)
        if (await _offers.HasActiveTripAsync(driverId, exceptTripId: tripId))
            return (AcceptOutcome.DriverBusy, null);

        var accepted = await _offers.TryAcceptAsync(tripId, driverId, _options.OfferTtlSeconds);
        if (accepted is null)
        {
            if (await _offers.HasActiveTripAsync(driverId, exceptTripId: tripId))
                return (AcceptOutcome.DriverBusy, null);
            // Only reached on failure, so the extra lookup costs nothing on the happy path.
            var status = await _offers.GetStatusAsync(tripId, driverId);
            return status is null ? (AcceptOutcome.NotFound, null) : (AcceptOutcome.NotAvailable, null);
        }

        accepted.ExpiresAt = accepted.CreatedAt.AddSeconds(_options.OfferTtlSeconds);
        await PublishDriverAcceptedAsync(accepted);
        return (AcceptOutcome.Accepted, accepted);
    }

    public async Task<(StatusUpdateOutcome Outcome, DriverOffer? Offer)> UpdateStatusAsync(string tripId, string driverId, string action)
    {
        if (!TripStatusFlow.IsValidId(tripId) || !TripStatusFlow.IsValidId(driverId))
            return (StatusUpdateOutcome.InvalidRequest, null);

        var fromStatus = TripStatusFlow.PreviousStatus(action);
        if (fromStatus is null && action != "Accepted")
            return (StatusUpdateOutcome.InvalidTransition, null);

        var updated = fromStatus is null ? null : await _offers.TryAdvanceStatusAsync(tripId, driverId, fromStatus, action);
        if (updated is not null)
        {
            if (updated.Status == "Completed")
                await PublishTripCompletedAsync(updated);
            return (StatusUpdateOutcome.Updated, updated);
        }

        var current = await _offers.GetOfferAsync(tripId, driverId);
        if (current is null) return (StatusUpdateOutcome.NotFound, null);
        // Retries return the authoritative snapshot without changing timestamps or notifying twice.
        return (current.Status == action ? StatusUpdateOutcome.Updated : StatusUpdateOutcome.InvalidTransition, current);
    }

    public async Task<(StatusUpdateOutcome Outcome, DriverOffer? Offer)> DecideAsync(string tripId, string driverId, bool cancel)
    {
        if (!TripStatusFlow.IsValidId(tripId) || !TripStatusFlow.IsValidId(driverId))
            return (StatusUpdateOutcome.InvalidRequest, null);
        var updated = await _offers.TryDriverDecisionAsync(tripId, driverId, cancel, _options.OfferTtlSeconds);
        if (updated is not null) return (StatusUpdateOutcome.Updated, updated);
        var current = await _offers.GetOfferAsync(tripId, driverId);
        return current is null ? (StatusUpdateOutcome.NotFound, null) : (StatusUpdateOutcome.InvalidTransition, current);
    }

    // Retain the existing completed-trip read window; it never makes the driver busy.
    private const int RecentlyCompletedHours = 6;

    public async Task<DriverOffer?> GetCurrentTripAsync(string driverId)
    {
        var trip = await _offers.GetCurrentTripForDriverAsync(driverId, RecentlyCompletedHours);
        if (trip is not null)
            trip.ExpiresAt = trip.CreatedAt.AddSeconds(_options.OfferTtlSeconds);   // same shape accept returns
        return trip;
    }

    // The accept is already committed by the time we get here, and retrying it would
    // fail (the offer is no longer Pending) — so a failure to notify the rider is
    // logged rather than turned into a failed accept.
    private async Task PublishDriverAcceptedAsync(DriverOffer offer)
    {
        try
        {
            // Vehicle details only enrich the rider's notification, so a failed lookup just leaves them blank.
            var vehicle = await _driverProfiles.FindAsync(offer.DriverId);

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

    // Completing a trip is what makes it payable. Like the accept above, the status change is already
    // committed, so both notifications are only queued (neither waits on the network) and a failure here
    // is logged rather than turned into a failed status update.
    private async Task PublishTripCompletedAsync(DriverOffer trip)
    {
        try
        {
            // payment-service is told directly (with retries), so the rider can pay.
            _completionNotifier.Enqueue(trip);

            // goride-payment also reads TRIP_COMPLETED off the events topic and halts on one without a fare,
            // so a fare-less trip (already logged by the notifier) isn't published there either.
            if (trip.Fare is null) return;

            // For goride-notification. Deliberately NOT the eventId sent to payment-service directly: that
            // body has a different payload shape, and payment-service rejects an eventId it has seen with
            // different data. Same occurredAt and fare, though, so the two are consistent if both arrive.
            await _publisher.PublishAsync(new TripEvent
            {
                EventType = "TRIP_COMPLETED",
                TripId = trip.TripId,
                RiderId = trip.RiderId,
                DriverId = trip.DriverId,
                OccurredAt = trip.CompletedAt ?? DateTime.UtcNow,
                Payload = new TripEventPayload
                {
                    Fare = trip.Fare,
                    PickupLocation = trip.PickupLocation,
                    DropoffLocation = trip.DropoffLocation,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trip {TripId} was completed by driver {DriverId} but TRIP_COMPLETED could not be sent.",
                trip.TripId, trip.DriverId);
        }
    }
}
