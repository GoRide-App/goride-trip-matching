using GoRide.Trip.Models;

namespace GoRide.Trip.Data;

/// <summary>
/// Tracks which drivers a trip request was offered to. One row per (trip, driver);
/// Status is one of Pending, Accepted, Declined, Expired. Deliberately
/// keyed by a plain trip_id string with no FK to `trips` — nothing in this service
/// owns that table's schema yet.
/// </summary>
public interface IDriverOfferRepository
{
    /// <summary>Idempotent — creates driver_offers if it doesn't exist.</summary>
    Task EnsureSchemaAsync();

    Task<bool> HasAcceptedOfferAsync(string tripId);

    /// <summary>Records a Pending offer; re-offering the same trip to the same driver resets it to Pending.</summary>
    Task CreatePendingAsync(string tripId, string driverId, string riderId, double distanceKm,
        string? pickupLocation, double? pickupLat, double? pickupLng,
        string? dropoffLocation, double? dropoffLat, double? dropoffLng, decimal? fare);

    /// <summary>The driver's Pending offers created within the last ttlSeconds, newest first.</summary>
    Task<List<DriverOffer>> GetPendingForDriverAsync(string driverId, int ttlSeconds);

    /// <summary>
    /// Atomically flips this driver's offer from Pending to Accepted — but only if it is
    /// still Pending and younger than ttlSeconds. Returns the offer when it was accepted,
    /// or null when it wasn't (missing, already decided, or expired).
    /// </summary>
    Task<DriverOffer?> TryAcceptAsync(string tripId, string driverId, int ttlSeconds);

    /// <summary>The offer's current Status, or null when there is no such offer.</summary>
    Task<string?> GetStatusAsync(string tripId, string driverId);

    /// <summary>Every offer made for a trip, whatever its status.</summary>
    Task<List<DriverOffer>> GetOffersForTripAsync(string tripId);

    /// <summary>
    /// Atomically advances the accepted offer from fromStatus to toStatus and stamps the matching
    /// timestamp column (arrived_at / started_at / completed_at) -- only if it is still exactly
    /// fromStatus, so two concurrent requests (or a stale retry) can't both apply. Returns the
    /// updated offer, or null when the transition wasn't valid (wrong driver, wrong current status,
    /// or no such offer at all).
    /// </summary>
    Task<DriverOffer?> TryAdvanceStatusAsync(string tripId, string driverId, string fromStatus, string toStatus);
}

public class DriverOfferRepository : IDriverOfferRepository
{
    private const string OfferColumns =
        "trip_id, driver_id, rider_id, status, distance_km, pickup_location, pickup_lat, pickup_lng, " +
        "dropoff_location, dropoff_lat, dropoff_lng, fare, created_at, arrived_at, started_at, completed_at";

    private readonly IDbConnectionFactory _dbFactory;

    public DriverOfferRepository(IDbConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task EnsureSchemaAsync()
    {
        await ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS driver_offers (
                trip_id          VARCHAR(64)   NOT NULL,
                driver_id        VARCHAR(64)   NOT NULL,
                rider_id         VARCHAR(64)   NOT NULL,
                status           VARCHAR(16)   NOT NULL DEFAULT 'Pending',
                distance_km      DOUBLE        NOT NULL,
                pickup_location  VARCHAR(255)  NULL,
                pickup_lat       DOUBLE        NULL,
                pickup_lng       DOUBLE        NULL,
                dropoff_location VARCHAR(255)  NULL,
                dropoff_lat      DOUBLE        NULL,
                dropoff_lng      DOUBLE        NULL,
                fare             DECIMAL(10,2) NULL,
                created_at       DATETIME(3)   NOT NULL,
                decided_at       DATETIME(3)   NULL,
                arrived_at       DATETIME(3)   NULL,
                started_at       DATETIME(3)   NULL,
                completed_at     DATETIME(3)   NULL,
                PRIMARY KEY (trip_id, driver_id),
                INDEX idx_driver_offers_driver_status (driver_id, status)
            )");
    }

    // "Accepted" and later stages (Arrived/InProgress/Completed) are all "this offer is the trip",
    // as opposed to Pending/Declined/Expired -- a trip already at any of those stages must never
    // be re-offered or re-searched.
    private const string WonStatuses = "('Accepted', 'Arrived', 'InProgress', 'Completed')";

    public async Task<bool> HasAcceptedOfferAsync(string tripId)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM driver_offers WHERE trip_id = @tripId AND status IN {WonStatuses} LIMIT 1";
        cmd.Parameters.AddWithValue("@tripId", tripId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public Task CreatePendingAsync(string tripId, string driverId, string riderId, double distanceKm,
        string? pickupLocation, double? pickupLat, double? pickupLng,
        string? dropoffLocation, double? dropoffLat, double? dropoffLng, decimal? fare) =>
        ExecuteAsync(@"
            INSERT INTO driver_offers
                (trip_id, driver_id, rider_id, status, distance_km, pickup_location, pickup_lat, pickup_lng,
                 dropoff_location, dropoff_lat, dropoff_lng, fare, created_at, decided_at)
            VALUES
                (@tripId, @driverId, @riderId, 'Pending', @distanceKm, @pickup, @pickupLat, @pickupLng,
                 @dropoff, @dropoffLat, @dropoffLng, @fare, UTC_TIMESTAMP(3), NULL)
            ON DUPLICATE KEY UPDATE
                rider_id = VALUES(rider_id),
                status = 'Pending',
                distance_km = VALUES(distance_km),
                pickup_location = VALUES(pickup_location),
                pickup_lat = VALUES(pickup_lat),
                pickup_lng = VALUES(pickup_lng),
                dropoff_location = VALUES(dropoff_location),
                dropoff_lat = VALUES(dropoff_lat),
                dropoff_lng = VALUES(dropoff_lng),
                fare = VALUES(fare),
                created_at = VALUES(created_at),
                decided_at = NULL",
            ("@tripId", tripId), ("@driverId", driverId), ("@riderId", riderId), ("@distanceKm", distanceKm),
            ("@pickup", pickupLocation ?? (object)DBNull.Value),
            ("@pickupLat", pickupLat.HasValue ? (object)pickupLat.Value : DBNull.Value),
            ("@pickupLng", pickupLng.HasValue ? (object)pickupLng.Value : DBNull.Value),
            ("@dropoff", dropoffLocation ?? (object)DBNull.Value),
            ("@dropoffLat", dropoffLat.HasValue ? (object)dropoffLat.Value : DBNull.Value),
            ("@dropoffLng", dropoffLng.HasValue ? (object)dropoffLng.Value : DBNull.Value),
            ("@fare", fare.HasValue ? (object)fare.Value : DBNull.Value));

    public Task<List<DriverOffer>> GetPendingForDriverAsync(string driverId, int ttlSeconds) =>
        QueryAsync($@"
            SELECT {OfferColumns} FROM driver_offers
            WHERE driver_id = @driverId
              AND status = 'Pending'
              AND created_at >= (UTC_TIMESTAMP(3) - INTERVAL @ttl SECOND)
            ORDER BY created_at DESC",
            ("@driverId", driverId), ("@ttl", ttlSeconds));

    public async Task<DriverOffer?> TryAcceptAsync(string tripId, string driverId, int ttlSeconds)
    {
        // One conditional UPDATE: the WHERE clause is the whole validation, and MySQL
        // applies a single statement atomically, so two accepts of the same offer can't both win.
        var updated = await ExecuteAsync(@"
            UPDATE driver_offers
            SET status = 'Accepted', decided_at = UTC_TIMESTAMP(3)
            WHERE trip_id = @tripId AND driver_id = @driverId
              AND status = 'Pending'
              AND created_at >= (UTC_TIMESTAMP(3) - INTERVAL @ttl SECOND)",
            ("@tripId", tripId), ("@driverId", driverId), ("@ttl", ttlSeconds));

        if (updated == 0) return null;

        var rows = await QueryAsync(
            $"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId AND driver_id = @driverId",
            ("@tripId", tripId), ("@driverId", driverId));
        return rows.SingleOrDefault();
    }

    public async Task<string?> GetStatusAsync(string tripId, string driverId)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM driver_offers WHERE trip_id = @tripId AND driver_id = @driverId";
        cmd.Parameters.AddWithValue("@tripId", tripId);
        cmd.Parameters.AddWithValue("@driverId", driverId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public Task<List<DriverOffer>> GetOffersForTripAsync(string tripId) =>
        QueryAsync($"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId", ("@tripId", tripId));

    public async Task<DriverOffer?> TryAdvanceStatusAsync(string tripId, string driverId, string fromStatus, string toStatus)
    {
        // toStatus only ever comes from DriverOfferService's own fixed action->status map (never
        // straight from the HTTP request), so it's safe to pick the timestamp column from it here.
        var timestampColumn = toStatus switch
        {
            "Arrived" => "arrived_at",
            "InProgress" => "started_at",
            "Completed" => "completed_at",
            _ => throw new ArgumentOutOfRangeException(nameof(toStatus), toStatus, "Unknown trip status."),
        };

        // Same pattern as TryAcceptAsync: the WHERE clause (still fromStatus, right driver) is the
        // whole validation, applied atomically in one UPDATE.
        var updated = await ExecuteAsync($@"
            UPDATE driver_offers
            SET status = @toStatus, {timestampColumn} = UTC_TIMESTAMP(3)
            WHERE trip_id = @tripId AND driver_id = @driverId AND status = @fromStatus",
            ("@tripId", tripId), ("@driverId", driverId), ("@fromStatus", fromStatus), ("@toStatus", toStatus));

        if (updated == 0) return null;

        var rows = await QueryAsync(
            $"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId AND driver_id = @driverId",
            ("@tripId", tripId), ("@driverId", driverId));
        return rows.SingleOrDefault();
    }

    private async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<DriverOffer>> QueryAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        var offers = new List<DriverOffer>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            offers.Add(new DriverOffer
            {
                TripId = reader.GetString(0),
                DriverId = reader.GetString(1),
                RiderId = reader.GetString(2),
                Status = reader.GetString(3),
                DistanceKm = reader.GetDouble(4),
                PickupLocation = reader.IsDBNull(5) ? null : reader.GetString(5),
                PickupLat = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                PickupLng = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                DropoffLocation = reader.IsDBNull(8) ? null : reader.GetString(8),
                DropoffLat = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                DropoffLng = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                Fare = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                CreatedAt = DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc),
                ArrivedAt = reader.IsDBNull(13) ? null : DateTime.SpecifyKind(reader.GetDateTime(13), DateTimeKind.Utc),
                StartedAt = reader.IsDBNull(14) ? null : DateTime.SpecifyKind(reader.GetDateTime(14), DateTimeKind.Utc),
                CompletedAt = reader.IsDBNull(15) ? null : DateTime.SpecifyKind(reader.GetDateTime(15), DateTimeKind.Utc),
            });
        }
        return offers;
    }
}
