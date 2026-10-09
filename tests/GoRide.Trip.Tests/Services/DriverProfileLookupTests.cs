using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

namespace GoRide.Trip.Tests.Services;

/// <summary>
/// Every accept and every rider status poll used to fetch the whole active-driver list from
/// identity-auth. These pin that the lookup is cached, and that a failure still never breaks
/// the caller.
/// </summary>
public class DriverProfileLookupTests
{
    private readonly Mock<IActiveDriversService> _activeDrivers = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    private DriverProfileLookup CreateLookup() =>
        new(_activeDrivers.Object, _cache, NullLogger<DriverProfileLookup>.Instance);

    [Fact]
    public async Task RepeatedLookups_OnlyCallIdentityOnce_EvenForOtherDriversInTheList()
    {
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ReturnsAsync(new List<ActiveDriver>
        {
            new() { DriverId = "d1", VehiclePlate = "CAB-4521" },
            new() { DriverId = "d2", VehiclePlate = "CAB-9999" },
        });

        var first = await CreateLookup().FindAsync("d1");
        var again = await CreateLookup().FindAsync("d1");     // e.g. the rider's next poll, a new request scope
        var other = await CreateLookup().FindAsync("d2");

        Assert.Equal("CAB-4521", first!.VehiclePlate);
        Assert.Equal("CAB-4521", again!.VehiclePlate);
        Assert.Equal("CAB-9999", other!.VehiclePlate);
        _activeDrivers.Verify(a => a.GetActiveDriversAsync(null), Times.Once);
    }

    [Fact]
    public async Task IdentityDown_ReturnsNull_AndDoesNotRetryOnEveryPoll()
    {
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ThrowsAsync(new TaskCanceledException("timed out"));

        Assert.Null(await CreateLookup().FindAsync("d1"));
        Assert.Null(await CreateLookup().FindAsync("d1"));

        _activeDrivers.Verify(a => a.GetActiveDriversAsync(null), Times.Once);
    }

    [Fact]
    public async Task UnlistedDriver_ReturnsNull()
    {
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ReturnsAsync(new List<ActiveDriver>
        {
            new() { DriverId = "someone-else" },
        });

        Assert.Null(await CreateLookup().FindAsync("d1"));
    }
}
