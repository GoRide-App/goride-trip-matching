using GoRide.Trip.Models;
using GoRide.Trip.Services;
using Microsoft.AspNetCore.Mvc;

namespace GoRide.Trip.Controllers;

[ApiController]
[Route("matching")]
public class TripMatchingController : ControllerBase
{
    private readonly IDriverMatchingService _matchingService;

    public TripMatchingController(IDriverMatchingService matchingService)
    {
        _matchingService = matchingService;
    }

    /// <summary>
    /// Available drivers of the requested vehicle type within the fixed search radius
    /// of the pickup point, nearest first. "No drivers" is a normal outcome, so this
    /// returns 200 with Matched=false rather than an error.
    /// </summary>
    [HttpPost("nearby-drivers")]
    [ProducesResponseType(typeof(FindNearbyDriversResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> FindNearbyDrivers([FromBody] FindNearbyDriversRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.VehicleTypeCode))
            return BadRequest(new { error = "vehicleTypeCode is required." });
        if (request.PickupLat is < -90 or > 90)
            return BadRequest(new { error = "pickupLat must be between -90 and 90." });
        if (request.PickupLng is < -180 or > 180)
            return BadRequest(new { error = "pickupLng must be between -180 and 180." });

        return Ok(await _matchingService.FindNearbyDriversAsync(request));
    }
}
