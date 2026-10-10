using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using GoRide.Trip.Models;
using GoRide.Trip.Services;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;

namespace GoRide.Trip.Controllers;

/// <summary>The driver's side of a ride request.</summary>
[ApiController]
[Route("matching/offers")]
public class DriverOffersController : ControllerBase
{
    private readonly IDriverOfferService _offerService;
    private readonly ILogger<DriverOffersController> _logger;

    public DriverOffersController(IDriverOfferService offerService, ILogger<DriverOffersController> logger)
    {
        _offerService = offerService;
        _logger = logger;
    }

    /// <summary>
    /// Ride requests waiting for this driver — pending and not yet expired, newest first.
    /// The driver app polls this to learn about new requests.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<DriverOffer>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetPending([FromQuery] string? driverId)
    {
        if (string.IsNullOrWhiteSpace(driverId))
            return BadRequest(new { error = "driverId is required." });

        try
        {
            return Ok(await _offerService.GetPendingAsync(driverId));
        }
        catch (MySqlException ex)
        {
            _logger.LogError(ex, "Could not load pending offers for driver {DriverId}.", driverId);
            return Unavailable();
        }
    }

    /// <summary>
    /// The driver's current trip, so their app can pick it back up after a reload. 200 with the
    /// same shape accept returns, for a trip that is Accepted, Arrived or InProgress -- or one
    /// Completed in the last 6 hours (the app checks whether it has been paid); 204 when there's none.
    /// </summary>
    [HttpGet("active")]
    [ProducesResponseType(typeof(DriverOffer), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetActive([FromQuery] string? driverId)
    {
        if (string.IsNullOrWhiteSpace(driverId))
            return BadRequest(new { error = "driverId is required." });

        try
        {
            var trip = await _offerService.GetCurrentTripAsync(driverId);
            return trip is null ? NoContent() : Ok(trip);
        }
        catch (MySqlException ex)
        {
            _logger.LogError(ex, "Could not load the current trip for driver {DriverId}.", driverId);
            return Unavailable();
        }
    }

    /// <summary>
    /// The driver accepts a ride request. 200 with the accepted offer; 404 if this
    /// driver was never offered the trip; 409 if the offer is no longer available
    /// (expired, or already decided), or -- with code DRIVER_BUSY -- if the driver
    /// is already on another trip.
    /// </summary>
    [HttpPost("{tripId}/accept")]
    [ProducesResponseType(typeof(DriverOffer), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Accept(string tripId, [FromBody] AcceptOfferRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DriverId))
            return BadRequest(new { error = "driverId is required." });

        try
        {
            var (outcome, offer) = await _offerService.AcceptAsync(tripId, request.DriverId);
            return outcome switch
            {
                AcceptOutcome.Accepted => Ok(offer),
                AcceptOutcome.NotFound => NotFound(new { error = "This driver has no offer for that trip." }),
                AcceptOutcome.DriverBusy => Conflict(new
                {
                    error = "You already have an active trip. Finish it before accepting another ride.",
                    code = "DRIVER_BUSY",
                }),
                _ => Conflict(new { error = "This ride request is no longer available." }),
            };
        }
        catch (MySqlException ex)
        {
            _logger.LogError(ex, "Could not accept trip {TripId} for driver {DriverId}.", tripId, request.DriverId);
            return Unavailable();
        }
    }

    /// <summary>
    /// The driver advances their accepted trip one stage: Arrived, then InProgress, then Completed.
    /// 200 with the updated offer; 404 if this driver has no offer for the trip; 409 if the
    /// requested stage isn't the one immediately after its current status. Completing a trip also
    /// tells payment-service (in the background, so not before this responds) so the rider can pay.
    /// </summary>
    [HttpPost("{tripId}/status")]
    [ProducesResponseType(typeof(DriverOffer), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UpdateStatus(
        [FromRoute, Required, StringLength(64)] string tripId,
        [FromBody] UpdateTripStatusRequest request)
    {
        try
        {
            var (outcome, offer) = await _offerService.UpdateStatusAsync(tripId, request.DriverId, request.Action);
            return outcome switch
            {
                StatusUpdateOutcome.Updated => Ok(offer),
                StatusUpdateOutcome.InvalidRequest => StatusError(StatusCodes.Status400BadRequest,
                    "INVALID_REQUEST", "tripId and driverId are required and must not exceed 64 characters."),
                StatusUpdateOutcome.NotFound => StatusError(StatusCodes.Status404NotFound,
                    "TRIP_NOT_FOUND", "This driver has no offer for that trip."),
                _ => StatusError(StatusCodes.Status409Conflict, "INVALID_TRIP_TRANSITION",
                    request.Action == "InProgress"
                        ? "This ride cannot be started. It must have arrived and must not already be started or completed."
                        : "This trip cannot advance to that status."),
            };
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            _logger.LogError(ex, "Could not update status for trip {TripId}, driver {DriverId}.", tripId, request.DriverId);
            return StatusError(StatusCodes.Status503ServiceUnavailable,
                "OFFERS_STORE_UNAVAILABLE", "Could not reach the offers store right now. Please try again.");
        }
    }

    private ObjectResult StatusError(int status, string code, string error)
    {
        var problem = new ProblemDetails { Status = status, Title = error };
        problem.Extensions["code"] = code;
        // Keep the existing error property for clients that already display it.
        problem.Extensions["error"] = error;
        return StatusCode(status, problem);
    }

    private ObjectResult Unavailable() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = "Could not reach the offers store right now. Please try again." });
}
