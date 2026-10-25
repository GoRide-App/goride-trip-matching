using System.Net.Http.Json;
using GoRide.Trip.Models;

namespace GoRide.Trip.Services;

public interface IActiveDriversService {
    Task<List<ActiveDriver>> GetActiveDriversAsync(string? vehicleType);
}

/// <summary>
/// Calls identity-auth's internal "active drivers" endpoint (GET /api/internal-drivers)
/// and returns the result as-is. Just a proxy — trip-matching doesn't store any driver
/// data itself, identity-auth is the source of truth for it.
/// </summary>
public class ActiveDriversService : IActiveDriversService {
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public ActiveDriversService(HttpClient httpClient, IConfiguration configuration) {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<List<ActiveDriver>> GetActiveDriversAsync(string? vehicleType) {
        var apiKey = _configuration["InternalServices:ApiKey"]
            ?? throw new InvalidOperationException(
                "Missing configuration: InternalServices:ApiKey (set it in appsettings.Development.json locally, " +
                "or as the InternalServices__ApiKey environment variable in Docker/Azure)");

        var url = "/api/internal-drivers";
        if (!string.IsNullOrWhiteSpace(vehicleType)) {
            url += $"?vehicleType={Uri.EscapeDataString(vehicleType)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Internal-Api-Key", apiKey);

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var drivers = await response.Content.ReadFromJsonAsync<List<ActiveDriver>>();
        return drivers ?? new List<ActiveDriver>();
    }
}
