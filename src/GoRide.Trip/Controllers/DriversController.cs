using GoRide.Trip.Services;
using Microsoft.AspNetCore.Mvc;

namespace GoRide.Trip.Controllers;

[ApiController]
[Route("drivers")]
public class DriversController : ControllerBase {
    private readonly IActiveDriversService _activeDriversService;

    public DriversController(IActiveDriversService activeDriversService) {
        _activeDriversService = activeDriversService;
    }

    /// <summary>
    /// Lists currently active drivers, optionally filtered by vehicle type
    /// (e.g. GET /drivers/active?vehicleType=TUK). Backed by identity-auth —
    /// this endpoint just passes the request through and returns the result.
    /// </summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActiveDrivers([FromQuery] string? vehicleType) {
        var drivers = await _activeDriversService.GetActiveDriversAsync(vehicleType);
        return Ok(drivers);
    }
}
