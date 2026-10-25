using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GoRide.Trip.Controllers;
using GoRide.Trip.Data;
using GoRide.Trip.Events;
using GoRide.Trip.Models;
using GoRide.Trip.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GoRide.Trip.Tests.Controllers;

public class DriverOffersControllerTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"driverId\":null,\"action\":\"InProgress\"}")]
    [InlineData("{\"driverId\":\" \",\"action\":\"InProgress\"}")]
    [InlineData("{\"driverId\":\"d1\"}")]
    [InlineData("{\"driverId\":\"d1\",\"action\":null}")]
    [InlineData("{\"driverId\":\"d1\",\"action\":\"inprogress\"}")]
    [InlineData("{\"driverId\":\"d1\",\"action\":\"Cancelled\"}")]
    [InlineData("{\"driverId\":\"d1\",\"action\":1}")]
    public async Task InvalidBody_ReturnsValidationErrorsWithoutCallingService(string body)
    {
        await using var app = new OffersApp();
        using var client = app.CreateClient();

        var response = await client.PostAsync("/matching/offers/trip-1/status",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("errors", out _));
        app.Service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverlongId_ReturnsBadRequestWithoutCallingService(bool longTripId)
    {
        await using var app = new OffersApp();
        using var client = app.CreateClient();
        var longId = new string('x', 65);
        var tripId = longTripId ? longId : "trip-1";

        var response = await client.PostAsJsonAsync($"/matching/offers/{tripId}/status",
            new { driverId = longTripId ? "d1" : longId, action = "InProgress" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        app.Service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(StatusUpdateOutcome.NotFound, HttpStatusCode.NotFound, "TRIP_NOT_FOUND")]
    [InlineData(StatusUpdateOutcome.InvalidTransition, HttpStatusCode.Conflict, "INVALID_TRIP_TRANSITION")]
    [InlineData(StatusUpdateOutcome.InvalidRequest, HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    public async Task RejectedUpdate_ReturnsStructuredError(StatusUpdateOutcome outcome, HttpStatusCode status, string code)
    {
        await using var app = new OffersApp();
        app.Service.Setup(s => s.UpdateStatusAsync("trip-1", "d1", "InProgress"))
            .ReturnsAsync((outcome, (DriverOffer?)null));
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/matching/offers/trip-1/status",
            new { driverId = "d1", action = "InProgress" });

        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableStore_Returns503WithoutExceptionDetails(bool timeout)
    {
        await using var app = new OffersApp();
        app.Service.Setup(s => s.UpdateStatusAsync("trip-1", "d1", "InProgress"))
            .ThrowsAsync(timeout ? new TimeoutException("private-db-details") : new StoreException());
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/matching/offers/trip-1/status",
            new { driverId = "d1", action = "InProgress" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("OFFERS_STORE_UNAVAILABLE", body);
        Assert.DoesNotContain("private-db-details", body);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(64)]
    public async Task ValidStart_ReturnsUpdatedOffer(int idLength)
    {
        await using var app = new OffersApp();
        var startedAt = DateTime.UtcNow;
        var tripId = new string('t', idLength);
        var driverId = new string('d', idLength);
        app.Service.Setup(s => s.UpdateStatusAsync(tripId, driverId, "InProgress"))
            .ReturnsAsync((StatusUpdateOutcome.Updated, new DriverOffer
            {
                TripId = tripId,
                DriverId = driverId,
                Status = "InProgress",
                StartedAt = startedAt,
            }));
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync($"/matching/offers/{tripId}/status",
            new { driverId, action = "InProgress" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var offer = await response.Content.ReadFromJsonAsync<DriverOffer>();
        Assert.Equal("InProgress", offer!.Status);
        Assert.Equal(startedAt, offer.StartedAt);
    }

    private sealed class StoreException() : DbException("private-db-details");

    public sealed class OffersApp : WebApplicationFactory<Program>
    {
        public Mock<IDriverOfferService> Service { get; } = new(MockBehavior.Strict);
        public Mock<IRideRequestService> Rides { get; } = new(MockBehavior.Strict);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Error-response tests must not depend on access to the host's Windows Event Log.
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services =>
            {
                var repository = new Mock<IDriverOfferRepository>();
                repository.Setup(r => r.EnsureSchemaAsync()).Returns(Task.CompletedTask);
                services.RemoveAll<IDriverOfferRepository>();
                services.AddSingleton(repository.Object);
                services.RemoveAll<IDriverOfferService>();
                services.AddSingleton(Service.Object);
                services.RemoveAll<IRideRequestService>();
                services.AddSingleton(Rides.Object);

                // Hosted event delivery must never construct a real Kafka producer in HTTP tests.
                var producer = new Mock<IKafkaProducerService>(MockBehavior.Strict);
                producer.Setup(p => p.Flush(It.IsAny<TimeSpan>())).Returns(0);
                services.RemoveAll<IKafkaProducerService>();
                services.AddSingleton(producer.Object);
                services.RemoveAll<IDriverProfileLookup>();
                services.AddSingleton(Mock.Of<IDriverProfileLookup>(MockBehavior.Strict));
                services.RemoveAll<ITripCompletionNotifier>();
                services.AddSingleton(Mock.Of<ITripCompletionNotifier>(MockBehavior.Strict));
            });
        }
    }
}

/// <summary>Pins the HTTP contract the driver app relies on for the busy-driver guard and trip restore.</summary>
public class DriverOffersControllerContractTests
{
    private readonly Mock<IDriverOfferService> _service = new();

    private DriverOffersController CreateController() =>
        new(_service.Object, NullLogger<DriverOffersController>.Instance);

    // Serialized the way ASP.NET Core writes response bodies (camelCase).
    private static JsonElement Json(object? value) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task Accept_DriverBusy_Is409_WithTheDriverBusyCode()
    {
        _service.Setup(s => s.AcceptAsync("trip-2", "d1")).ReturnsAsync((AcceptOutcome.DriverBusy, (DriverOffer?)null));

        var result = await CreateController().Accept("trip-2", new AcceptOfferRequest { DriverId = "d1" });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = Json(conflict.Value);
        Assert.Equal("DRIVER_BUSY", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Accept_NoLongerAvailable_StaysAPlain409()
    {
        _service.Setup(s => s.AcceptAsync("trip-1", "d1")).ReturnsAsync((AcceptOutcome.NotAvailable, (DriverOffer?)null));

        var result = await CreateController().Accept("trip-1", new AcceptOfferRequest { DriverId = "d1" });

        var body = Json(Assert.IsType<ConflictObjectResult>(result).Value);
        Assert.False(body.TryGetProperty("code", out _));
    }

    [Fact]
    public async Task Active_WithATrip_Is200_WithTheTrip()
    {
        var trip = new DriverOffer { TripId = "trip-1", DriverId = "d1", RiderId = "rider-1", Status = "Arrived", Fare = 725.50m };
        _service.Setup(s => s.GetCurrentTripAsync("d1")).ReturnsAsync(trip);

        var result = await CreateController().GetActive("d1");

        var body = Json(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("trip-1", body.GetProperty("tripId").GetString());
        Assert.Equal("Arrived", body.GetProperty("status").GetString());
        Assert.Equal("rider-1", body.GetProperty("riderId").GetString());
        Assert.Equal(725.50m, body.GetProperty("fare").GetDecimal());
    }

    [Fact]
    public async Task Active_NoTrip_Is204()
    {
        _service.Setup(s => s.GetCurrentTripAsync("d1")).ReturnsAsync((DriverOffer?)null);

        Assert.IsType<NoContentResult>(await CreateController().GetActive("d1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Active_WithoutADriverId_Is400(string? driverId)
    {
        Assert.IsType<BadRequestObjectResult>(await CreateController().GetActive(driverId));
        _service.Verify(s => s.GetCurrentTripAsync(It.IsAny<string>()), Times.Never);
    }
}
