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
    /// The driver accepts a ride request. 200 with the accepted offer; 404 if this
    /// driver was never offered the trip; 409 if the offer is no longer available
    /// (expired, or already decided).
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
                _ => Conflict(new { error = "This ride request is no longer available." }),
            };
        }
        catch (MySqlException ex)
        {
            _logger.LogError(ex, "Could not accept trip {TripId} for driver {DriverId}.", tripId, request.DriverId);
            return Unavailable();
        }
    }

    private ObjectResult Unavailable() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { error = "Could not reach the offers store right now. Please try again." });
}
