using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GoRide.Trip.Data;
using GoRide.Trip.Models;
using GoRide.Trip.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    private sealed class OffersApp : WebApplicationFactory<Program>
    {
        public Mock<IDriverOfferService> Service { get; } = new(MockBehavior.Strict);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var repository = new Mock<IDriverOfferRepository>();
                repository.Setup(r => r.EnsureSchemaAsync()).Returns(Task.CompletedTask);
                services.RemoveAll<IDriverOfferRepository>();
                services.AddSingleton(repository.Object);
                services.RemoveAll<IDriverOfferService>();
                services.AddSingleton(Service.Object);
            });
        }
    }
}
