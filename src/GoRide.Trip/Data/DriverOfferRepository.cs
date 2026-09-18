namespace GoRide.Trip.Data;

/// <summary>
/// Tracks which drivers a trip request was offered to. One row per (trip, driver);
/// Status is one of Pending, Accepted, Declined, Expired, Failed. Deliberately
/// keyed by a plain trip_id string with no FK to `trips` — nothing in this service
/// owns that table's schema yet.
/// </summary>
public interface IDriverOfferRepository
{
    /// <summary>Idempotent — creates driver_offers if it doesn't exist.</summary>
    Task EnsureSchemaAsync();

    Task<bool> HasAcceptedOfferAsync(string tripId);

    /// <summary>Records a Pending offer; re-offering the same trip to the same driver resets it to Pending.</summary>
    Task CreatePendingAsync(string tripId, string driverId, string riderId, double distanceKm);

    /// <summary>Marks an offer that couldn't be delivered, so it can never be accepted.</summary>
    Task MarkFailedAsync(string tripId, string driverId);
}

public class DriverOfferRepository : IDriverOfferRepository
{
    private readonly IDbConnectionFactory _dbFactory;

    public DriverOfferRepository(IDbConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task EnsureSchemaAsync()
    {
        await ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS driver_offers (
                trip_id     VARCHAR(64) NOT NULL,
                driver_id   VARCHAR(64) NOT NULL,
                rider_id    VARCHAR(64) NOT NULL,
                status      VARCHAR(16) NOT NULL DEFAULT 'Pending',
                distance_km DOUBLE      NOT NULL,
                created_at  DATETIME(3) NOT NULL,
                decided_at  DATETIME(3) NULL,
                PRIMARY KEY (trip_id, driver_id),
                INDEX idx_driver_offers_driver_status (driver_id, status)
            )");
    }

    public async Task<bool> HasAcceptedOfferAsync(string tripId)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM driver_offers WHERE trip_id = @tripId AND status = 'Accepted' LIMIT 1";
        cmd.Parameters.AddWithValue("@tripId", tripId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public Task CreatePendingAsync(string tripId, string driverId, string riderId, double distanceKm) =>
        ExecuteAsync(@"
            INSERT INTO driver_offers (trip_id, driver_id, rider_id, status, distance_km, created_at, decided_at)
            VALUES (@tripId, @driverId, @riderId, 'Pending', @distanceKm, UTC_TIMESTAMP(3), NULL)
            ON DUPLICATE KEY UPDATE
                rider_id = VALUES(rider_id),
                status = 'Pending',
                distance_km = VALUES(distance_km),
                created_at = VALUES(created_at),
                decided_at = NULL",
            ("@tripId", tripId), ("@driverId", driverId), ("@riderId", riderId), ("@distanceKm", distanceKm));

    public Task MarkFailedAsync(string tripId, string driverId) =>
        ExecuteAsync(@"
            UPDATE driver_offers
            SET status = 'Failed', decided_at = UTC_TIMESTAMP(3)
            WHERE trip_id = @tripId AND driver_id = @driverId AND status = 'Pending'",
            ("@tripId", tripId), ("@driverId", driverId));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
