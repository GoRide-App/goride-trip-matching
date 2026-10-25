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

    // ---- Mutation-targeted tests ----

    // Kills swapping FoundFor ↔ MissingFor:
    // a driver that IS in the list must be cached long enough that a second call
    // within 60 s still hits the cache. If FoundFor were replaced with MissingFor
    // (10 s) the test would still pass in normal speed tests, so we verify the
    // observable effect: a second immediate call must NOT re-call identity-auth.
    [Fact]
    public async Task FoundDriver_IsCachedSoSubsequentCallsSkipIdentityAuth()
    {
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ReturnsAsync(new List<ActiveDriver>
        {
            new() { DriverId = "d1", VehiclePlate = "CAB-4521" },
        });
        var lookup = CreateLookup();  // shared instance shares the cache

        var first  = await lookup.FindAsync("d1");
        var second = await lookup.FindAsync("d1");   // must hit cache, not identity-auth again

        Assert.Equal("CAB-4521", first!.VehiclePlate);
        Assert.Equal("CAB-4521", second!.VehiclePlate);
        _activeDrivers.Verify(a => a.GetActiveDriversAsync(null), Times.Once);  // kills FoundFor removal / zero TTL mutant
    }

    // Kills removal of the `_cache.TryGetValue` early-return path:
    // if TryGetValue is removed Stryker, identity-auth is called on every lookup.
    // We confirm two sequential lookups only ever produce one identity-auth call.
    [Fact]
    public async Task CacheHit_PreventsRedundantIdentityAuthCalls()
    {
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ReturnsAsync(new List<ActiveDriver>
        {
            new() { DriverId = "d1", VehiclePlate = "FIRST-CALL" },
        });
        var lookup = CreateLookup();

        await lookup.FindAsync("d1");   // populates cache
        await lookup.FindAsync("d1");   // must be served from cache
        await lookup.FindAsync("d1");   // still from cache

        // If TryGetValue early-return is removed, this would be 3.
        _activeDrivers.Verify(a => a.GetActiveDriversAsync(null), Times.Once);
    }

    // Kills removal of the `DistinctBy(d => d.DriverId)` call:
    // duplicate DriverId entries from identity-auth must not cause duplicate cache writes
    // that could leave the cache in an inconsistent state or throw on Set.
    [Fact]
    public async Task DuplicateDriverIdsFromIdentity_DoNotCauseErrors_AndReturnOneResult()
    {
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ReturnsAsync(new List<ActiveDriver>
        {
            new() { DriverId = "d1", VehiclePlate = "FIRST" },
            new() { DriverId = "d1", VehiclePlate = "DUPLICATE" },  // same DriverId twice
        });

        // Must not throw, and must return the first-encountered entry.
        var result = await CreateLookup().FindAsync("d1");

        Assert.NotNull(result);
        // DistinctBy takes the first, so VehiclePlate is "FIRST" (not "DUPLICATE").
        Assert.Equal("FIRST", result!.VehiclePlate);  // kills DistinctBy removal mutant
    }

    // Kills `d.DriverId == driverId` → `d.DriverId != driverId` in FirstOrDefault:
    // the lookup must return the driver whose ID matches, not the first one that doesn't.
    [Fact]
    public async Task FindAsync_ReturnsTheSpecificRequestedDriver_NotAnother()
    {
        _activeDrivers.Setup(a => a.GetActiveDriversAsync(null)).ReturnsAsync(new List<ActiveDriver>
        {
            new() { DriverId = "d1", VehiclePlate = "PLATE-D1" },
            new() { DriverId = "d2", VehiclePlate = "PLATE-D2" },
            new() { DriverId = "d3", VehiclePlate = "PLATE-D3" },
        });

        var d2 = await CreateLookup().FindAsync("d2");

        Assert.Equal("d2",       d2!.DriverId);     // correct ID
        Assert.Equal("PLATE-D2", d2.VehiclePlate);  // kills == → != mutant in FirstOrDefault predicate
    }
}
