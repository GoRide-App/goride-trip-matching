using Microsoft.AspNetCore.Mvc;
using GoRide.Trip.Data;

namespace GoRide.Trip.Controllers;

/// <summary>
/// The very first thing to prove working when standing up a new service: can it
/// actually reach the database. Hit GET /health after `dotnet run` (or after
/// `docker compose up`) — {"status":"healthy"} means the walking skeleton works.
/// </summary>
[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly IConfiguration _configuration;

    public HealthController(IDbConnectionFactory dbFactory, IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            await using var conn = _dbFactory.CreateConnection();
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();

            return Ok(new
            {
                status = "healthy",
                database = "connected",
                databaseName = _configuration["Db:Database"],
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                status = "unhealthy",
                error = ex.Message,
            });
        }
    }
}
