using GoRide.Trip.Data;
using GoRide.Trip.Models;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Xunit;

namespace GoRide.Trip.Tests.Data;

[Trait("Category", "MySql")]
public class TripLifecycleRepositoryTests(MySqlFixture database) : IClassFixture<MySqlFixture>
{
    private DriverOfferRepository Repository(int minutes = 120) =>
        new(database, Options.Create(new TripLifecycleOptions { StaleAfterMinutes = minutes }));

    private async Task<string> Pending(string driver, string? trip = null)
    {
        trip ??= Guid.NewGuid().ToString();
        await Repository().CreatePendingAsync(trip, driver, "rider", 1, null, null, null, null, null, null, 100);
        return trip;
    }

    private async Task<string> Accepted(string driver, string stage = "Accepted")
    {
        var trip = await Pending(driver);
        Assert.NotNull(await Repository().TryAcceptAsync(trip, driver, 60));
        if (stage is "Arrived" or "InProgress" or "Completed")
            Assert.NotNull(await Repository().TryAdvanceStatusAsync(trip, driver, "Accepted", "Arrived"));
        if (stage is "InProgress" or "Completed")
            Assert.NotNull(await Repository().TryAdvanceStatusAsync(trip, driver, "Arrived", "InProgress"));
        if (stage == "Completed")
            Assert.NotNull(await Repository().TryAdvanceStatusAsync(trip, driver, "InProgress", "Completed"));
        return trip;
    }

    private async Task Age(string trip, int minutes)
    {
        await using var conn = database.CreateConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE driver_offers SET created_at = UTC_TIMESTAMP(3) - INTERVAL @minutes MINUTE,
                decided_at = UTC_TIMESTAMP(3) - INTERVAL @minutes MINUTE,
                arrived_at = IF(arrived_at IS NULL, NULL, UTC_TIMESTAMP(3) - INTERVAL @minutes MINUTE)
            WHERE trip_id = @trip
            """;
        cmd.Parameters.AddWithValue("@trip", trip);
        cmd.Parameters.AddWithValue("@minutes", minutes);
        await cmd.ExecuteNonQueryAsync();
    }

    [MySqlTheory]
    [InlineData("Pending")]
    [InlineData("Accepted")]
    [InlineData("Arrived")]
    public async Task RiderCancel_ClosesOffersAndFreesDriver(string stage)
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Pending(driver);
        await Pending("other-" + driver, trip);
        if (stage != "Pending") Assert.NotNull(await Repository().TryAcceptAsync(trip, driver, 60));
        if (stage == "Arrived") Assert.NotNull(await Repository().TryAdvanceStatusAsync(trip, driver, "Accepted", "Arrived"));
        Assert.Equal("Cancelled", (await Repository().CancelRideAsync(trip, "rider")).Outcome);
        Assert.All(await Repository().GetOffersForTripAsync(trip), o => Assert.Equal("Cancelled", o.Status));
        Assert.Null(await Repository().GetCurrentTripForDriverAsync(driver, 6));
        Assert.False(await Repository().HasActiveTripAsync(driver, ""));
        Assert.Empty(await Repository().GetPendingForDriverAsync("other-" + driver, 60));
        Assert.Null(await Repository().TryAcceptAsync(trip, driver, 60));
        Assert.Equal("Cancelled", (await Repository().CancelRideAsync(trip, "rider")).Outcome);
        // Late delivery cannot recreate a cancelled request.
        await Pending("late-" + driver, trip);
        Assert.Equal(2, (await Repository().GetOffersForTripAsync(trip)).Count);
    }

    [MySqlTheory]
    [InlineData("Accepted")]
    [InlineData("Arrived")]
    public async Task DriverCancel_PreservesWhoCancelledAndFreesDriver(string stage)
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Accepted(driver, stage);
        var offer = await Repository().TryDriverDecisionAsync(trip, driver, true, 60);
        Assert.Equal("Cancelled", offer!.Status);
        Assert.Equal("Driver", (await Repository().GetOfferAsync(trip, driver))!.CancelledBy);
        Assert.Null(await Repository().GetCurrentTripForDriverAsync(driver, 6));
        Assert.NotNull(await Repository().TryDriverDecisionAsync(trip, driver, true, 60));
        Assert.NotNull(await Repository().TryAcceptAsync(await Pending(driver), driver, 60));
    }

    [MySqlTheory]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    public async Task StartedOrCompletedTrip_CannotBeCancelled(string stage)
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Accepted(driver, stage);
        Assert.Null(await Repository().TryDriverDecisionAsync(trip, driver, true, 60));
        Assert.Equal(("InvalidTransition", stage), await Repository().CancelRideAsync(trip, "rider"));
        Assert.Equal(stage, (await Repository().GetOfferAsync(trip, driver))!.Status);
    }

    [MySqlFact]
    public async Task Decline_RemovesOnlyOwnPendingOffer()
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Pending(driver);
        var other = Guid.NewGuid().ToString();
        await Pending(other, trip);
        Assert.Equal("Declined", (await Repository().TryDriverDecisionAsync(trip, driver, false, 60))!.Status);
        Assert.Empty(await Repository().GetPendingForDriverAsync(driver, 60));
        Assert.Single(await Repository().GetPendingForDriverAsync(other, 60));
        Assert.NotNull(await Repository().TryAcceptAsync(trip, other, 60));
        Assert.Null(await Repository().TryDriverDecisionAsync(trip, other, false, 60));
    }

    [MySqlTheory]
    [InlineData("Accepted", 121, 120, true)]
    [InlineData("Arrived", 121, 120, true)]
    [InlineData("Accepted", 10, 120, false)]
    [InlineData("Arrived", 10, 5, true)]
    [InlineData("InProgress", 121, 120, false)]
    [InlineData("Completed", 121, 120, false)]
    public async Task ReadTimeExpiry_UsesConfigAndDoesNotExpireStartedTrips(string stage, int age, int limit, bool expires)
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Accepted(driver, stage);
        await Age(trip, age);
        var active = await Repository(limit).GetCurrentTripForDriverAsync(driver, 6);
        Assert.Equal(expires, active is null);
        Assert.Equal(expires ? "Expired" : stage, (await Repository(limit).GetOfferAsync(trip, driver))!.Status);
        if (expires) Assert.NotNull(await Repository(limit).TryAcceptAsync(await Pending(driver), driver, 60));
    }

    [MySqlFact]
    public async Task RecentArrival_RestartsTheStaleWindow()
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Accepted(driver);
        await Age(trip, 100);
        Assert.NotNull(await Repository().TryAdvanceStatusAsync(trip, driver, "Accepted", "Arrived"));
        Assert.Equal("Arrived", (await Repository(5).GetCurrentTripForDriverAsync(driver, 6))!.Status);
    }

    [MySqlFact]
    public async Task ConcurrentAccepts_OneTripPerDriver()
    {
        var driver = Guid.NewGuid().ToString();
        var first = await Pending(driver);
        var second = await Pending(driver);
        var outcomes = await Task.WhenAll(Repository().TryAcceptAsync(first, driver, 60), Repository().TryAcceptAsync(second, driver, 60));
        Assert.Single(outcomes, o => o is not null);
        var loser = outcomes[0] is null ? first : second;
        var other = Guid.NewGuid().ToString();
        await Pending(other, loser);
        Assert.NotNull(await Repository().TryAcceptAsync(loser, other, 60));
    }

    [MySqlFact]
    public async Task ConcurrentDrivers_FirstValidAcceptWins()
    {
        var d1 = Guid.NewGuid().ToString();
        var d2 = Guid.NewGuid().ToString();
        var trip = await Pending(d1);
        await Pending(d2, trip);
        var outcomes = await Task.WhenAll(Repository().TryAcceptAsync(trip, d1, 60), Repository().TryAcceptAsync(trip, d2, 60));
        Assert.Single(outcomes, o => o is not null);
        Assert.Single(await Repository().GetOffersForTripAsync(trip), o => o.Status == "Accepted");
    }

    [MySqlFact]
    public async Task InvalidAccept_DoesNotStealClaim()
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Pending(driver);
        Assert.Null(await Repository().TryAcceptAsync(trip, "missing", 60));
        Assert.NotNull(await Repository().TryAcceptAsync(trip, driver, 60));
    }

    [MySqlFact]
    public async Task CancellationAndStart_OnlyOneWins()
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Accepted(driver, "Arrived");
        var cancel = Repository().CancelRideAsync(trip, "rider");
        var start = Repository().TryAdvanceStatusAsync(trip, driver, "Arrived", "InProgress");
        await Task.WhenAll(cancel, start);
        var current = (await Repository().GetOfferAsync(trip, driver))!;
        Assert.True(current.Status is "Cancelled" or "InProgress");
        Assert.Equal(current.Status == "Cancelled", cancel.Result.Outcome == "Cancelled");
        Assert.Equal(current.Status == "InProgress", start.Result is not null);
    }

    [MySqlFact]
    public async Task WrongOwner_CannotCancelOrDecline()
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Accepted(driver);
        Assert.Equal("NotFound", (await Repository().CancelRideAsync(trip, "wrong-rider")).Outcome);
        Assert.Null(await Repository().TryDriverDecisionAsync(trip, "wrong-driver", true, 60));
        Assert.Null(await Repository().TryDriverDecisionAsync(trip, "wrong-driver", false, 60));
        Assert.Equal("Accepted", (await Repository().GetOfferAsync(trip, driver))!.Status);
    }

    [MySqlFact]
    public async Task ConcurrentCancellationAndAcceptance_CannotLeaveAnActiveOffer()
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Pending(driver);
        await Task.WhenAll(Repository().TryAcceptAsync(trip, driver, 60), Repository().CancelRideAsync(trip, "rider"));
        Assert.Equal("Cancelled", (await Repository().GetOfferAsync(trip, driver))!.Status);
        Assert.Null(await Repository().GetCurrentTripForDriverAsync(driver, 6));
    }

    [MySqlFact]
    public async Task ExpiryAndStart_CannotReviveAStaleTrip()
    {
        var driver = Guid.NewGuid().ToString();
        var trip = await Accepted(driver, "Arrived");
        await Age(trip, 121);
        var start = Repository().TryAdvanceStatusAsync(trip, driver, "Arrived", "InProgress");
        await Task.WhenAll(start, Repository().GetCurrentTripForDriverAsync(driver, 6));
        Assert.Null(start.Result);
        Assert.Equal("Expired", (await Repository().GetOfferAsync(trip, driver))!.Status);
    }
}
