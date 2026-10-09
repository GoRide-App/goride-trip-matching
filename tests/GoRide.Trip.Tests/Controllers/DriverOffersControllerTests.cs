using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using GoRide.Trip.Controllers;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

namespace GoRide.Trip.Tests.Controllers;

/// <summary>Pins the HTTP contract the driver app relies on for the busy-driver guard and trip restore.</summary>
public class DriverOffersControllerTests
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
