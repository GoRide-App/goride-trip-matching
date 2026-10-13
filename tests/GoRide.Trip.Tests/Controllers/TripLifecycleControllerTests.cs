using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GoRide.Trip.Models;
using GoRide.Trip.Services;
using Moq;
using Xunit;

namespace GoRide.Trip.Tests.Controllers;

public class TripLifecycleControllerTests
{
    [Theory]
    [InlineData("Accepted")]
    [InlineData("Arrived")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    public async Task SameStage_Returns200WithOffer(string status)
    {
        await using var app = new DriverOffersControllerTests.OffersApp();
        app.Service.Setup(s => s.UpdateStatusAsync("trip", "driver", status))
            .ReturnsAsync((StatusUpdateOutcome.Updated, new DriverOffer { Status = status }));
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/matching/offers/trip/status", new { driverId = "driver", action = status });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(status, (await response.Content.ReadFromJsonAsync<DriverOffer>())!.Status);
    }

    [Fact]
    public async Task InvalidTransition_ContainsCurrentStatus()
    {
        await using var app = new DriverOffersControllerTests.OffersApp();
        app.Service.Setup(s => s.UpdateStatusAsync("trip", "driver", "Arrived"))
            .ReturnsAsync((StatusUpdateOutcome.InvalidTransition, new DriverOffer { Status = "InProgress" }));
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/matching/offers/trip/status", new { driverId = "driver", action = "Arrived" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_TRIP_TRANSITION", body.GetProperty("code").GetString());
        Assert.Equal("InProgress", body.GetProperty("currentStatus").GetString());
    }

    [Theory]
    [InlineData("cancel", true, "Cancelled")]
    [InlineData("decline", false, "Declined")]
    public async Task DriverDecision_RouteReturnsOffer(string route, bool cancel, string status)
    {
        await using var app = new DriverOffersControllerTests.OffersApp();
        app.Service.Setup(s => s.DecideAsync("trip", "driver", cancel))
            .ReturnsAsync((StatusUpdateOutcome.Updated, new DriverOffer { Status = status }));
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync($"/matching/offers/trip/{route}", new { driverId = "driver", reason = "Cannot reach pickup" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(status, (await response.Content.ReadFromJsonAsync<DriverOffer>())!.Status);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("decline")]
    public async Task DriverDecision_InvalidBodyIs400(string route)
    {
        await using var app = new DriverOffersControllerTests.OffersApp();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/matching/offers/trip/{route}", new { driverId = " " })).StatusCode);
        app.Service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true, StatusUpdateOutcome.InvalidTransition, 409)]
    [InlineData(false, StatusUpdateOutcome.NotFound, 404)]
    public async Task DriverDecision_ReportsRejection(bool cancel, StatusUpdateOutcome outcome, int expected)
    {
        await using var app = new DriverOffersControllerTests.OffersApp();
        app.Service.Setup(s => s.DecideAsync("trip", "driver", cancel))
            .ReturnsAsync((outcome, new DriverOffer { Status = "InProgress" }));
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync($"/matching/offers/trip/{(cancel ? "cancel" : "decline")}", new { driverId = "driver" });
        Assert.Equal(expected, (int)response.StatusCode);
        if (expected == 409)
            Assert.Equal("InProgress", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currentStatus").GetString());
    }

    [Theory]
    [InlineData("Cancelled", "Cancelled", 200)]
    [InlineData("InvalidTransition", "InProgress", 409)]
    [InlineData("NotFound", null, 404)]
    public async Task RiderCancel_RouteReportsOutcome(string outcome, string? status, int expected)
    {
        await using var app = new DriverOffersControllerTests.OffersApp();
        app.Rides.Setup(s => s.CancelAsync("trip", "rider")).ReturnsAsync((outcome, status));
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/matching/ride-requests/trip/cancel", new { riderId = "rider" });
        Assert.Equal(expected, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (expected == 200) Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        if (expected == 409) Assert.Equal("InProgress", body.GetProperty("currentStatus").GetString());
    }

    [Fact]
    public async Task RiderCancel_ValidatesRiderId()
    {
        await using var app = new DriverOffersControllerTests.OffersApp();
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/matching/ride-requests/trip/cancel", new { })).StatusCode);
        app.Rides.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("DriverCancelled")]
    [InlineData("Expired")]
    public async Task Poll_ReportsTerminalStatus(string status)
    {
        await using var app = new DriverOffersControllerTests.OffersApp();
        app.Rides.Setup(s => s.GetStatusAsync("trip")).ReturnsAsync(new RideRequestStatus { TripId = "trip", Status = status });
        using var client = app.CreateClient();
        Assert.Equal(status, (await client.GetFromJsonAsync<RideRequestStatus>("/matching/ride-requests/trip"))!.Status);
    }

    [Fact]
    public async Task ExpiredTrip_IsNotRestored()
    {
        await using var app = new DriverOffersControllerTests.OffersApp();
        app.Service.Setup(s => s.GetCurrentTripAsync("driver")).ReturnsAsync((DriverOffer?)null);
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/matching/offers/active?driverId=driver")).StatusCode);
    }
}
