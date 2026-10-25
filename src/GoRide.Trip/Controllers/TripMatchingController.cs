using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using GoRide.Trip.Models;
using GoRide.Trip.Services;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace GoRide.Trip.Controllers;

[ApiController]
[Route("matching")]
public class TripMatchingController : ControllerBase
{
    private readonly IDriverMatchingService _matchingService;
    private readonly IRideRequestService _rideRequestService;
    private readonly ILogger<TripMatchingController> _logger;

    public TripMatchingController(
        IDriverMatchingService matchingService,
        IRideRequestService rideRequestService,
        ILogger<TripMatchingController> logger)
    {
        _matchingService = matchingService;
        _rideRequestService = rideRequestService;
        _logger = logger;
    }

    /// <summary>
    /// Available drivers of the requested vehicle type within the fixed search radius
    /// of the pickup point, nearest first. "No drivers" is a normal outcome, so this
    /// returns 200 with Matched=false rather than an error.
    /// </summary>
    [HttpPost("nearby-drivers")]
    [ProducesResponseType(typeof(FindNearbyDriversResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> FindNearbyDrivers([FromBody] FindNearbyDriversRequest request)
    {
        if (Validate(request) is { } invalid) return invalid;

        try
        {
            return Ok(await _matchingService.FindNearbyDriversAsync(request));
        }
        catch (Exception ex) when (IsDependencyFailure(ex))
        {
            return DependencyUnavailable(ex, request.TripId);
        }
    }

    /// <summary>
    /// Finds nearby available drivers for a trip and delivers the ride request to
    /// them (a Pending offer is recorded and a RIDE_REQUESTED event is published to
    /// Kafka for each). Returns the drivers it was delivered to; Matched=false when
    /// there are none.
    /// </summary>
    [HttpPost("ride-requests")]
    [ProducesResponseType(typeof(FindNearbyDriversResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RequestRide([FromBody] FindNearbyDriversRequest request)
    {
        if (Validate(request, isRideRequest: true) is { } invalid) return invalid;

        try
        {
            var (result, error) = await _rideRequestService.DeliverAsync(request);
            if (error is not null)
                return Conflict(new { error });

            return Ok(result);
        }
        catch (MySqlException ex)
        {
            _logger.LogError(ex, "Could not deliver ride request for trip {TripId}.", request.TripId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Could not deliver the ride request right now. Please try again." });
        }
        catch (Exception ex) when (IsDependencyFailure(ex))
        {
            return DependencyUnavailable(ex, request.TripId);
        }
    }

    /// <summary>
    /// Where a delivered ride request stands: Searching, Accepted (with the driver), or
    /// NoDriver. The rider's app polls this. 404 if nothing was ever offered for the trip.
    /// </summary>
    [HttpGet("ride-requests/{tripId}")]
    [ProducesResponseType(typeof(RideRequestStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetRideRequestStatus(string tripId)
    {
        try
        {
            var status = await _rideRequestService.GetStatusAsync(tripId);
            return status is null ? NotFound(new { error = "No ride request found for that trip." }) : Ok(status);
        }
        catch (MySqlException ex)
        {
            _logger.LogError(ex, "Could not load ride request status for trip {TripId}.", tripId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Could not reach the offers store right now. Please try again." });
        }
    }

    [HttpPost("ride-requests/{tripId}/cancel")]
    public async Task<IActionResult> CancelRide([FromRoute, Required, StringLength(64)] string tripId,
        [FromBody] CancelRideRequest request)
    {
        try
        {
            var (outcome, currentStatus) = await _rideRequestService.CancelAsync(tripId, request.RiderId);
            return outcome switch
            {
                "Cancelled" => Ok(new RideRequestStatus { TripId = tripId, Status = "Cancelled" }),
                "InvalidRequest" => BadRequest(new { error = "tripId and riderId are required and must not exceed 64 characters." }),
                "NotFound" => NotFound(new { error = "No ride request found for this rider and trip." }),
                _ => Conflict(new { code = "INVALID_TRIP_TRANSITION", error = "This ride can only be cancelled before it starts.", currentStatus }),
            };
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            _logger.LogError(ex, "Could not cancel ride request {TripId}.", tripId);
            return StatusCode(503, new { error = "Could not reach the offers store right now. Please try again." });
        }
    }

    // identity-auth or goride-location said no, couldn't be reached, didn't answer in time (HttpClient
    // reports its timeout as a TaskCanceledException), or this service itself is missing required config
    // (e.g. InternalServices:ApiKey) to call them. All of that is an infrastructure problem, not "no
    // drivers nearby", so say so instead of returning a bare, unexplained 500.
    private static bool IsDependencyFailure(Exception ex) =>
        ex is HttpRequestException or InvalidOperationException or TaskCanceledException;

    private ObjectResult DependencyUnavailable(Exception ex, string? tripId)
    {
        _logger.LogError(ex, "A service needed to find drivers failed (trip {TripId}): {Message}", tripId, ex.Message);
        return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = "A service needed to find drivers is unavailable right now. Please try again." });
    }

    private IActionResult? Validate(FindNearbyDriversRequest request, bool isRideRequest = false)
    {
        if (isRideRequest)
        {
            if (string.IsNullOrWhiteSpace(request.PickupLocation))
                return BadRequest(new { error = "pickupLocation is required." });
            if (string.IsNullOrWhiteSpace(request.DropoffLocation))
                return BadRequest(new { error = "dropoffLocation is required." });
            if (string.IsNullOrWhiteSpace(request.TripId))
                return BadRequest(new { error = "tripId is required." });
            if (string.IsNullOrWhiteSpace(request.RiderId))
                return BadRequest(new { error = "riderId is required." });
        }

        if (string.IsNullOrWhiteSpace(request.VehicleTypeCode))
            return BadRequest(new { error = "vehicleTypeCode is required." });
        if (request.PickupLat is < -90 or > 90)
            return BadRequest(new { error = "pickupLat must be between -90 and 90." });
        if (request.PickupLng is < -180 or > 180)
            return BadRequest(new { error = "pickupLng must be between -180 and 180." });

        return null;
    }
}
