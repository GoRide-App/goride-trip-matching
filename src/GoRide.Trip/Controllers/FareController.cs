using Microsoft.AspNetCore.Mvc;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

namespace GoRide.Trip.Controllers;

[ApiController]
[Route("fare")]
public class FareController : ControllerBase
{
    private readonly IFareCalculationService _fareCalculationService;

    public FareController(IFareCalculationService fareCalculationService)
    {
        _fareCalculationService = fareCalculationService;
    }

    [HttpPost("estimate")]
    public async Task<IActionResult> EstimateFares([FromBody] FareEstimateRequest request)
    {
        if (request.StartLat is null || request.StartLng is null || request.EndLat is null || request.EndLng is null)
        {
            return BadRequest(new { error = "startLat, startLng, endLat, and endLng are all required." });
        }

        var options = await _fareCalculationService.EstimateFaresAsync(
            request.StartLat.Value, request.StartLng.Value, request.EndLat.Value, request.EndLng.Value);

        return Ok(options);
    }
}