using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Xunit;
using GoRide.Trip.Services;

namespace GoRide.Trip.Tests.Services;

/// <summary>
/// Pins the request trip-matching sends to identity-auth's /api/internal-drivers. That contract
/// (API-key header + ?vehicleType= query string) once drifted from what identity-auth accepted,
/// which silently broke driver matching.
/// </summary>
public class ActiveDriversServiceTests
{
    private sealed class StubHandler(HttpStatusCode status, string body = "[]") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ActiveDriversService Service, StubHandler Handler) Create(HttpStatusCode status, string body = "[]")
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://identity.test") };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["InternalServices:ApiKey"] = "super-secret" })
            .Build();
        return (new ActiveDriversService(http, config), handler);
    }

    [Fact]
    public async Task SendsTheApiKeyHeader_AndTheVehicleTypeInTheQueryString()
    {
        var (service, handler) = Create(HttpStatusCode.OK);

        await service.GetActiveDriversAsync("TUK");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal("/api/internal-drivers?vehicleType=TUK", handler.Request.RequestUri!.PathAndQuery);
        Assert.Equal("super-secret", handler.Request.Headers.GetValues("X-Internal-Api-Key").Single());
    }

    [Fact]
    public async Task WithoutAVehicleType_NoQueryStringIsSent()
    {
        var (service, handler) = Create(HttpStatusCode.OK);

        await service.GetActiveDriversAsync(null);

        Assert.Equal("/api/internal-drivers", handler.Request!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task ParsesTheDriversReturned()
    {
        var (service, _) = Create(HttpStatusCode.OK,
            "[{\"driverId\":\"d1\",\"vehicleTypeCode\":\"TUK\",\"vehiclePlate\":\"CAB-1\",\"vehicleMake\":\"Bajaj\",\"vehicleModel\":\"RE\"}]");

        var drivers = await service.GetActiveDriversAsync("TUK");

        var d = Assert.Single(drivers);
        Assert.Equal("d1", d.DriverId);
        Assert.Equal("CAB-1", d.VehiclePlate);
    }

    [Fact]
    public async Task WhenIdentityRejectsTheCall_ItThrows_SoTheFailureIsNotMistakenForNoDrivers()
    {
        var (service, _) = Create(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetActiveDriversAsync("TUK"));
    }
}
