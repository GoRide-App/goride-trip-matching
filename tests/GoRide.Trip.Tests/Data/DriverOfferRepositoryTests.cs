using GoRide.Trip.Data;
using GoRide.Trip.Models;
using MySqlConnector;
using Xunit;

namespace GoRide.Trip.Tests.Data;

// Opt in with SCRUM83_MYSQL pointing to a disposable MySQL server. Each run creates
// its own database, and only that generated database is removed after the tests.
[Trait("Category", "MySql")]
public class DriverOfferRepositoryTests(MySqlFixture database) : IClassFixture<MySqlFixture>
{
    private DriverOfferRepository Repository => new(database);

    [MySqlFact]
    public async Task AcceptedRide_CanAdvanceOneStepAtATime_ButCannotRestart()
    {
        var tripId = Guid.NewGuid().ToString();
        await Repository.CreatePendingAsync(tripId, "driver", "rider", 1,
            "Pickup", null, null, "Dropoff", null, null, 100);
        Assert.NotNull(await Repository.TryAcceptAsync(tripId, "driver", 60));

        Assert.Null(await Repository.TryAdvanceStatusAsync(tripId, "driver", "Arrived", "InProgress"));
        var arrived = await Repository.TryAdvanceStatusAsync(tripId, "driver", "Accepted", "Arrived");
        Assert.Equal("Arrived", arrived!.Status);
        Assert.NotNull(arrived.ArrivedAt);

        var started = await Repository.TryAdvanceStatusAsync(tripId, "driver", "Arrived", "InProgress");
        Assert.Equal("InProgress", started!.Status);
        Assert.NotNull(started.StartedAt);
        Assert.Equal(DateTimeKind.Utc, started.StartedAt!.Value.Kind);
        Assert.Equal(arrived.ArrivedAt, started.ArrivedAt);

        var completed = await Repository.TryAdvanceStatusAsync(tripId, "driver", "InProgress", "Completed");
        Assert.Equal("Completed", completed!.Status);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(started.StartedAt, completed.StartedAt);

        Assert.Null(await Repository.TryAdvanceStatusAsync(tripId, "driver", "Arrived", "InProgress"));
        Assert.Null(await Repository.TryAdvanceStatusAsync(tripId, "driver", "Completed", "InProgress"));
        var stored = Assert.Single(await Repository.GetOffersForTripAsync(tripId));
        Assert.Equal("Completed", stored.Status);
        Assert.Equal(completed.CompletedAt, stored.CompletedAt);
        Assert.Equal(started.StartedAt, stored.StartedAt);
    }

    [MySqlTheory]
    [InlineData("Pending")]
    [InlineData("Accepted")]
    [InlineData("Declined")]
    [InlineData("Expired")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("InProgress")]
    [InlineData("Completed")]
    public async Task InvalidPriorStatus_CannotStartOrChangeTimestamps(string status)
    {
        var tripId = await SeedAsync(status);
        var before = Assert.Single(await Repository.GetOffersForTripAsync(tripId));

        Assert.Null(await Repository.TryAdvanceStatusAsync(tripId, "driver", "Arrived", "InProgress"));

        var after = Assert.Single(await Repository.GetOffersForTripAsync(tripId));
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ArrivedAt, after.ArrivedAt);
        Assert.Equal(before.StartedAt, after.StartedAt);
        Assert.Equal(before.CompletedAt, after.CompletedAt);
    }

    [MySqlTheory]
    [InlineData(false, false, false)] // Has not arrived despite its status.
    [InlineData(true, true, false)]   // Has already started despite its status.
    [InlineData(true, false, true)]   // Has already completed despite its status.
    public async Task InconsistentTimestamps_CannotStart(bool arrived, bool started, bool completed)
    {
        var tripId = await SeedAsync("Arrived", arrived, started, completed);
        var before = Assert.Single(await Repository.GetOffersForTripAsync(tripId));

        Assert.Null(await Repository.TryAdvanceStatusAsync(tripId, "driver", "Arrived", "InProgress"));

        var after = Assert.Single(await Repository.GetOffersForTripAsync(tripId));
        Assert.Equal("Arrived", after.Status);
        Assert.Equal(before.StartedAt, after.StartedAt);
        Assert.Equal(before.CompletedAt, after.CompletedAt);
    }

    [MySqlTheory]
    [InlineData(null)]
    [InlineData("other-driver")]
    public async Task MissingOrDifferentClaimOwner_CannotStart(string? claimDriver)
    {
        var tripId = await SeedAsync("Arrived", claimDriver: claimDriver);

        Assert.Null(await Repository.TryAdvanceStatusAsync(tripId, "driver", "Arrived", "InProgress"));
        Assert.Equal("Arrived", await Repository.GetStatusAsync(tripId, "driver"));
    }

    [MySqlFact]
    public async Task WrongDriverOrUnknownTrip_CannotStart()
    {
        var tripId = await SeedAsync("Arrived");

        Assert.Null(await Repository.TryAdvanceStatusAsync(tripId, "other-driver", "Arrived", "InProgress"));
        Assert.Null(await Repository.TryAdvanceStatusAsync("missing", "driver", "Arrived", "InProgress"));
        Assert.Equal("Arrived", await Repository.GetStatusAsync(tripId, "driver"));
    }

    [MySqlFact]
    public async Task ConcurrentStarts_OnlyOneSucceeds_AndRetryPreservesTimestamp()
    {
        var tripId = await SeedAsync("Arrived");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Repository.TryAdvanceStatusAsync(tripId, "driver", "Arrived", "InProgress")));

        var winner = Assert.Single(attempts, offer => offer is not null);
        Assert.Equal("InProgress", winner!.Status);
        Assert.NotNull(winner.StartedAt);
        Assert.Null(await Repository.TryAdvanceStatusAsync(tripId, "driver", "Arrived", "InProgress"));
        var stored = Assert.Single(await Repository.GetOffersForTripAsync(tripId));
        Assert.Equal(winner.StartedAt, stored.StartedAt);
    }

    [MySqlFact]
    public async Task SqlMetacharactersInIds_AreTreatedAsLiteralValues()
    {
        const string literalId = "ride'; UPDATE driver_offers SET status='Completed'; --";
        await SeedAsync("Arrived", tripId: literalId);
        var otherTrip = await SeedAsync("Arrived");

        var started = await Repository.TryAdvanceStatusAsync(literalId, "driver", "Arrived", "InProgress");

        Assert.Equal(literalId, started!.TripId);
        Assert.Equal("InProgress", started.Status);
        Assert.Equal("Arrived", await Repository.GetStatusAsync(otherTrip, "driver"));
    }

    private async Task<string> SeedAsync(string status, bool arrived = true, bool started = false,
        bool completed = false, string? claimDriver = "driver", string? tripId = null)
    {
        tripId ??= Guid.NewGuid().ToString();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO driver_offers
                (trip_id, driver_id, rider_id, status, distance_km, created_at, arrived_at, started_at, completed_at)
            VALUES (@tripId, 'driver', 'rider', @status, 1, UTC_TIMESTAMP(3), @arrived, @started, @completed)
            """;
        var timestamp = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        command.Parameters.AddWithValue("@tripId", tripId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@arrived", arrived ? timestamp : (object)DBNull.Value);
        command.Parameters.AddWithValue("@started", started ? timestamp : (object)DBNull.Value);
        command.Parameters.AddWithValue("@completed", completed ? timestamp : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync();
        if (claimDriver is not null)
        {
            command.CommandText = "INSERT INTO trip_claims VALUES (@tripId, @driverId, UTC_TIMESTAMP(3))";
            command.Parameters.AddWithValue("@driverId", claimDriver);
            await command.ExecuteNonQueryAsync();
        }
        return tripId;
    }
}

public sealed class MySqlFixture : IDbConnectionFactory, IAsyncLifetime
{
    private readonly string? _server = Environment.GetEnvironmentVariable("SCRUM83_MYSQL");
    private readonly string _database = $"scrum83_{Guid.NewGuid():N}";
    private bool _created;

    public MySqlConnection CreateConnection() => new(new MySqlConnectionStringBuilder(_server!)
    {
        Database = _database,
    }.ConnectionString);

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server)) return;
        await ExecuteDatabaseCommandAsync($"CREATE DATABASE `{_database}`");
        _created = true;
        await new DriverOfferRepository(this).EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (_created)
            await ExecuteDatabaseCommandAsync($"DROP DATABASE `{_database}`");
    }

    private async Task ExecuteDatabaseCommandAsync(string sql)
    {
        await using var connection = new MySqlConnection(new MySqlConnectionStringBuilder(_server!)
        {
            Database = "",
        }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCRUM83_MYSQL")))
            Skip = "Set SCRUM83_MYSQL to a disposable MySQL server to run database tests.";
    }
}

public sealed class MySqlTheoryAttribute : TheoryAttribute
{
    public MySqlTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SCRUM83_MYSQL")))
            Skip = "Set SCRUM83_MYSQL to a disposable MySQL server to run database tests.";
    }
}
