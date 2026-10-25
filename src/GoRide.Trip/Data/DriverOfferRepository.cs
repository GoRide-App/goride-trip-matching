using System.Security.Cryptography;
using System.Text;
using GoRide.Trip.Models;
using Microsoft.Extensions.Options;
using MySqlConnector;

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

    Task<DriverOffer?> GetOfferAsync(string tripId, string driverId);
    Task<DriverOffer?> TryDriverDecisionAsync(string tripId, string driverId, bool cancel, int ttlSeconds);
    Task<(string Outcome, string? CurrentStatus)> CancelRideAsync(string tripId, string riderId);


    Task<bool> HasAcceptedOfferAsync(string tripId);

    /// <summary>Records a Pending offer; duplicates never reset a decision or its timestamps.</summary>
    Task CreatePendingAsync(string tripId, string driverId, string riderId, double distanceKm,
        string? pickupLocation, double? pickupLat, double? pickupLng,
        string? dropoffLocation, double? dropoffLat, double? dropoffLng, decimal? fare);

    /// <summary>The driver's Pending offers created within the last ttlSeconds, newest first.</summary>
    Task<List<DriverOffer>> GetPendingForDriverAsync(string driverId, int ttlSeconds);

    /// <summary>
    /// Accepts this driver's offer -- but only if it is still Pending and younger than
    /// ttlSeconds, AND no other driver has already accepted the same trip (SCRUM-62: if two
    /// drivers accept at the same instant, exactly one wins, never both). Returns the offer
    /// when it was accepted, or null when it wasn't (missing, already decided, expired, or
    /// beaten to it by another driver).
    /// </summary>
    Task<DriverOffer?> TryAcceptAsync(string tripId, string driverId, int ttlSeconds);

    /// <summary>The offer's current Status, or null when there is no such offer.</summary>
    Task<string?> GetStatusAsync(string tripId, string driverId);

    /// <summary>Every offer made for a trip, whatever its status.</summary>
    Task<List<DriverOffer>> GetOffersForTripAsync(string tripId);

    /// <summary>
    /// Atomically advances the accepted offer from fromStatus to toStatus and stamps the matching
    /// timestamp column (arrived_at / started_at / completed_at) -- only if it is still exactly
    /// fromStatus, the driver owns the trip claim, and the lifecycle timestamps allow it.
    /// Only adjacent forward steps are supported. Two concurrent requests (or a stale retry)
    /// can't both apply. The update and returned snapshot share a transaction. Returns the
    /// updated offer, or null when the transition wasn't valid (wrong driver, wrong current status,
    /// or no such offer at all).
    /// </summary>
    Task<DriverOffer?> TryAdvanceStatusAsync(string tripId, string driverId, string fromStatus, string toStatus);

    /// <summary>True when the driver is already on a trip (Accepted, Arrived or InProgress) other than exceptTripId.</summary>
    Task<bool> HasActiveTripAsync(string driverId, string exceptTripId);

    /// <summary>
    /// The driver's current trip: one still underway (Accepted, Arrived or InProgress), or else the newest
    /// one Completed within the last completedWithinHours. Null when there is neither.
    /// </summary>
    Task<DriverOffer?> GetCurrentTripForDriverAsync(string driverId, int completedWithinHours);
}

public class DriverOfferRepository : IDriverOfferRepository
{
    private const string OfferColumns =
        "trip_id, driver_id, rider_id, status, distance_km, pickup_location, pickup_lat, pickup_lng, " +
        "dropoff_location, dropoff_lat, dropoff_lng, fare, created_at, arrived_at, started_at, completed_at";

    private readonly IDbConnectionFactory _dbFactory;

    private readonly int _staleMinutes;

    public DriverOfferRepository(IDbConnectionFactory dbFactory, IOptions<TripLifecycleOptions>? lifecycle = null)
    {
        _dbFactory = dbFactory;
        _staleMinutes = lifecycle?.Value.StaleAfterMinutes ?? 120;
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

        // SCRUM-62: trip_id is the PRIMARY KEY here, so InnoDB itself enforces that only one
        // driver can ever successfully INSERT a claim row for a given trip -- see TryAcceptAsync.
        await ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS trip_claims (
                trip_id    VARCHAR(64) NOT NULL,
                driver_id  VARCHAR(64) NOT NULL,
                claimed_at DATETIME(3) NOT NULL,
                PRIMARY KEY (trip_id)
            )");
    }

    // "Accepted" and later stages (Arrived/InProgress/Completed) are all "this offer is the trip",
    // as opposed to Pending/Declined/Expired -- a trip already at any of those stages must never
    // be re-offered or re-searched.
    private const string ClosedToOffers = "('Accepted', 'Arrived', 'InProgress', 'Completed', 'Cancelled', 'DriverCancelled', 'Expired')";

    // The won statuses of a trip that is still underway -- the driver is busy with it.
    private const string ActiveStatuses = "('Accepted', 'Arrived', 'InProgress')";

    public async Task<bool> HasAcceptedOfferAsync(string tripId)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM driver_offers WHERE trip_id = @tripId AND status IN {ClosedToOffers} LIMIT 1";
        cmd.Parameters.AddWithValue("@tripId", tripId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public Task CreatePendingAsync(string tripId, string driverId, string riderId, double distanceKm,
        string? pickupLocation, double? pickupLat, double? pickupLng,
        string? dropoffLocation, double? dropoffLat, double? dropoffLng, decimal? fare) =>
        WithLocksAsync(new[] { "trip:" + tripId }, async conn =>
        {
            await using var transaction = await conn.BeginTransactionAsync();
            var existing = await QueryAsync(conn, transaction,
                $"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId FOR UPDATE", ("@tripId", tripId));
            if (existing.Any(o => o.RiderId != riderId || o.Status is "Accepted" or "Arrived" or "InProgress" or "Completed" or "Cancelled" or "Expired"))
                return 0;
            var result = await ExecuteAsync(conn, transaction, @"
            INSERT INTO driver_offers
                (trip_id, driver_id, rider_id, status, distance_km, pickup_location, pickup_lat, pickup_lng,
                 dropoff_location, dropoff_lat, dropoff_lng, fare, created_at, decided_at)
            VALUES
                (@tripId, @driverId, @riderId, 'Pending', @distanceKm, @pickup, @pickupLat, @pickupLng,
                 @dropoff, @dropoffLat, @dropoffLng, @fare, UTC_TIMESTAMP(3), NULL)
            ON DUPLICATE KEY UPDATE trip_id = VALUES(trip_id)",
            ("@tripId", tripId), ("@driverId", driverId), ("@riderId", riderId), ("@distanceKm", distanceKm),
            ("@pickup", pickupLocation ?? (object)DBNull.Value),
            ("@pickupLat", pickupLat.HasValue ? (object)pickupLat.Value : DBNull.Value),
            ("@pickupLng", pickupLng.HasValue ? (object)pickupLng.Value : DBNull.Value),
            ("@dropoff", dropoffLocation ?? (object)DBNull.Value),
            ("@dropoffLat", dropoffLat.HasValue ? (object)dropoffLat.Value : DBNull.Value),
            ("@dropoffLng", dropoffLng.HasValue ? (object)dropoffLng.Value : DBNull.Value),
            ("@fare", fare.HasValue ? (object)fare.Value : DBNull.Value));
            await transaction.CommitAsync();
            return result;
        });



    public Task<List<DriverOffer>> GetPendingForDriverAsync(string driverId, int ttlSeconds) =>
        QueryAsync($@"
            SELECT {OfferColumns} FROM driver_offers
            WHERE driver_id = @driverId
              AND status = 'Pending'
              AND NOT EXISTS (SELECT 1 FROM trip_claims WHERE trip_claims.trip_id = driver_offers.trip_id)
              AND created_at >= (UTC_TIMESTAMP(3) - INTERVAL @ttl SECOND)
            ORDER BY created_at DESC",
            ("@driverId", driverId), ("@ttl", ttlSeconds));

    public Task<DriverOffer?> TryAcceptAsync(string tripId, string driverId, int ttlSeconds) =>
        WithLocksAsync<DriverOffer?>(new[] { "driver:" + driverId, "trip:" + tripId }, async conn =>
        {
            // Driver and trip locks serialize competing accepts across service instances.
            await ExpireStaleAsync(driverId: driverId);
            await using var transaction = await conn.BeginTransactionAsync();
            var busy = await QueryAsync(conn, transaction,
                $"SELECT {OfferColumns} FROM driver_offers WHERE driver_id = @driverId AND status IN {ActiveStatuses} AND trip_id <> @tripId FOR UPDATE",
                ("@driverId", driverId), ("@tripId", tripId));
            if (busy.Count != 0) return null;
            var rows = await QueryAsync(conn, transaction,
                $"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId FOR UPDATE", ("@tripId", tripId));
            var offer = rows.SingleOrDefault(o => o.DriverId == driverId);
            if (offer?.Status != "Pending" || rows.Any(o => o.Status is "Accepted" or "Arrived" or "InProgress" or "Completed" or "Cancelled" or "Expired"))
                return null;

            // Claim and validation commit together: an invalid accept never blocks a valid one.
            var updated = await ExecuteAsync(conn, transaction, @"
                UPDATE driver_offers SET status = 'Accepted', decided_at = UTC_TIMESTAMP(3)
                WHERE trip_id = @tripId AND driver_id = @driverId AND status = 'Pending'
                  AND created_at > UTC_TIMESTAMP(3) - INTERVAL @ttl SECOND",
                ("@tripId", tripId), ("@driverId", driverId), ("@ttl", ttlSeconds));
            if (updated == 0) return null;
            try
            {
                await ExecuteAsync(conn, transaction,
                    "INSERT INTO trip_claims (trip_id, driver_id, claimed_at) VALUES (@tripId, @driverId, UTC_TIMESTAMP(3))",
                    ("@tripId", tripId), ("@driverId", driverId));
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                return null;
            }
            offer.Status = "Accepted";
            await transaction.CommitAsync();
            return offer;
        });

    public async Task<DriverOffer?> GetOfferAsync(string tripId, string driverId)
    {
        await ExpireStaleAsync(tripId: tripId);
        var rows = await QueryAsync($"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId AND driver_id = @driverId",
            ("@tripId", tripId), ("@driverId", driverId));
        return rows.SingleOrDefault();
    }

    public Task<DriverOffer?> TryDriverDecisionAsync(string tripId, string driverId, bool cancel, int ttlSeconds) =>
        WithLocksAsync<DriverOffer?>(new[] { "trip:" + tripId }, async conn =>
        {
            await ExpireStaleAsync(tripId: tripId);
            await using var transaction = await conn.BeginTransactionAsync();
            var rows = await QueryAsync(conn, transaction,
                $"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId FOR UPDATE", ("@tripId", tripId));
            var offer = rows.SingleOrDefault(o => o.DriverId == driverId);
            if (offer is null) return null;
            if (cancel ? offer.Status == "Cancelled" && offer.CancelledBy == "Driver" : offer.Status == "Declined")
                return offer;
            var allowed = cancel
                ? "status IN ('Accepted', 'Arrived') AND started_at IS NULL AND completed_at IS NULL"
                : "status = 'Pending' AND created_at > UTC_TIMESTAMP(3) - INTERVAL @ttl SECOND";
            if (!cancel && rows.Any(o => o.Status is "Accepted" or "Arrived" or "InProgress" or "Completed" or "Cancelled" or "Expired"))
                return null;
            var updated = await ExecuteAsync(conn, transaction, $@"
                UPDATE driver_offers SET status = @status, decided_at = UTC_TIMESTAMP(3)
                WHERE trip_id = @tripId AND driver_id = @driverId AND {allowed}",
                ("@status", cancel ? "DriverCancelled" : "Declined"), ("@tripId", tripId),
                ("@driverId", driverId), ("@ttl", ttlSeconds));
            if (updated == 0) return null;
            offer.Status = cancel ? "Cancelled" : "Declined";
            offer.CancelledBy = cancel ? "Driver" : null;
            await transaction.CommitAsync();
            return offer;
        });

    public Task<(string Outcome, string? CurrentStatus)> CancelRideAsync(string tripId, string riderId) =>
        WithLocksAsync<(string, string?)>(new[] { "trip:" + tripId }, async conn =>
        {
            await ExpireStaleAsync(tripId: tripId);
            await using var transaction = await conn.BeginTransactionAsync();
            var rows = await QueryAsync(conn, transaction,
                $"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId FOR UPDATE", ("@tripId", tripId));
            if (rows.Count == 0 || rows.Any(o => o.RiderId != riderId)) return ("NotFound", null);
            var closed = rows.FirstOrDefault(o => o.Status is "InProgress" or "Completed" or "Expired" || o.CancelledBy == "Driver");
            if (closed is not null)
                return ("InvalidTransition", closed.CancelledBy == "Driver" ? "DriverCancelled" : closed.Status);
            await ExecuteAsync(conn, transaction, @"
                UPDATE driver_offers SET status = 'Cancelled', decided_at = UTC_TIMESTAMP(3)
                WHERE trip_id = @tripId AND status IN ('Pending', 'Accepted', 'Arrived', 'Declined')",
                ("@tripId", tripId));
            await transaction.CommitAsync();
            return ("Cancelled", "Cancelled");
        });

    // Conditional UPDATE rechecks the stage after taking the row lock. InProgress never expires.
    private Task<int> ExpireStaleAsync(string? driverId = null, string? tripId = null) =>
        ExecuteAsync(@"
            UPDATE driver_offers SET status = 'Expired'
            WHERE status IN ('Accepted', 'Arrived')
              AND COALESCE(arrived_at, decided_at, created_at) <= UTC_TIMESTAMP(3) - INTERVAL @minutes MINUTE
              AND (@driverId IS NULL OR driver_id = @driverId)
              AND (@tripId IS NULL OR trip_id = @tripId)",
            ("@minutes", _staleMinutes), ("@driverId", (object?)driverId ?? DBNull.Value),
            ("@tripId", (object?)tripId ?? DBNull.Value));

    // MySQL advisory locks need no DDL grants and work across app replicas.
    // Always release before pooling the connection; hashes fit the 64-character name limit.
    private async Task<T> WithLocksAsync<T>(string[] keys, Func<MySqlConnection, Task<T>> action)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();
        var held = new List<string>();
        try
        {
            foreach (var key in keys)
            {
                var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conn.Database + ":" + key.ToUpperInvariant())));
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT GET_LOCK(@name, 10)";
                cmd.Parameters.AddWithValue("@name", name);
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) != 1) throw new TimeoutException("Trip lifecycle lock timed out.");
                held.Add(name);
            }
            return await action(conn);
        }
        finally
        {
            foreach (var name in held.AsEnumerable().Reverse())
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT RELEASE_LOCK(@name)";
                cmd.Parameters.AddWithValue("@name", name);
                await cmd.ExecuteScalarAsync();
            }
        }
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

    public async Task<List<DriverOffer>> GetOffersForTripAsync(string tripId)
    {
        await ExpireStaleAsync(tripId: tripId);
        return await QueryAsync($"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId", ("@tripId", tripId));
    }

    public async Task<DriverOffer?> TryAdvanceStatusAsync(string tripId, string driverId, string fromStatus, string toStatus)
    {
        // Enforce the state machine here too: no caller can request Completed -> InProgress.
        if (!TripStatusFlow.IsValidId(tripId) || !TripStatusFlow.IsValidId(driverId)
            || TripStatusFlow.PreviousStatus(toStatus) is not { } requiredStatus
            || fromStatus != requiredStatus)
            return null;

        await ExpireStaleAsync(tripId: tripId);

        // These fragments are fixed application values. All request values remain parameters.
        var timestampColumn = toStatus switch
        {
            "Arrived" => "arrived_at",
            "InProgress" => "started_at",
            "Completed" => "completed_at",
            _ => throw new ArgumentOutOfRangeException(nameof(toStatus), toStatus, "Unknown trip status."),
        };

        var timestampGuard = toStatus switch
        {
            "Arrived" => "arrived_at IS NULL AND started_at IS NULL",
            "InProgress" => "arrived_at IS NOT NULL AND started_at IS NULL",
            "Completed" => "arrived_at IS NOT NULL AND started_at IS NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(toStatus)),
        };

        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();
        await using var transaction = await conn.BeginTransactionAsync();

        // Keep the row locked until its snapshot has been read so the response cannot describe
        // a later transition made by another request. Completed timestamps are never reusable.
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $@"
            UPDATE driver_offers
            SET status = @toStatus, {timestampColumn} = UTC_TIMESTAMP(3)
            WHERE trip_id = @tripId AND driver_id = @driverId AND status = @fromStatus
              AND completed_at IS NULL AND {timestampGuard}
              AND (@fromStatus = 'InProgress' OR COALESCE(arrived_at, decided_at, created_at) > UTC_TIMESTAMP(3) - INTERVAL @minutes MINUTE)
              AND EXISTS (
                  SELECT 1 FROM trip_claims
                  WHERE trip_claims.trip_id = driver_offers.trip_id
                    AND trip_claims.driver_id = driver_offers.driver_id
              )";
        cmd.Parameters.AddWithValue("@tripId", tripId);
        cmd.Parameters.AddWithValue("@driverId", driverId);
        cmd.Parameters.AddWithValue("@fromStatus", fromStatus);
        cmd.Parameters.AddWithValue("@minutes", _staleMinutes);
        cmd.Parameters.AddWithValue("@toStatus", toStatus);
        var updated = await cmd.ExecuteNonQueryAsync();

        if (updated == 0) return null;

        // A failed read rolls back the update; a successful response always reflects a committed step.
        var rows = await QueryAsync(
            conn, transaction,
            $"SELECT {OfferColumns} FROM driver_offers WHERE trip_id = @tripId AND driver_id = @driverId",
            ("@tripId", tripId), ("@driverId", driverId));
        var offer = rows.Single();
        await transaction.CommitAsync();
        return offer;
    }

    public async Task<bool> HasActiveTripAsync(string driverId, string exceptTripId)
    {
        await ExpireStaleAsync(driverId: driverId);
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT 1 FROM driver_offers
            WHERE driver_id = @driverId AND status IN {ActiveStatuses} AND trip_id <> @tripId
            LIMIT 1";
        cmd.Parameters.AddWithValue("@driverId", driverId);
        cmd.Parameters.AddWithValue("@tripId", exceptTripId);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public async Task<DriverOffer?> GetCurrentTripForDriverAsync(string driverId, int completedWithinHours)
    {
        await ExpireStaleAsync(driverId: driverId);
        // A trip still underway always wins over a recently completed one (there should only ever be one
        // underway -- see DriverOfferService.AcceptAsync); within each group, the newest first.
        var rows = await QueryAsync($@"
            SELECT {OfferColumns} FROM driver_offers
            WHERE driver_id = @driverId
              AND (status IN {ActiveStatuses}
                   OR (status = 'Completed' AND completed_at >= (UTC_TIMESTAMP(3) - INTERVAL @hours HOUR)))
            ORDER BY status = 'Completed', created_at DESC
            LIMIT 1",
            ("@driverId", driverId), ("@hours", completedWithinHours));
        return rows.SingleOrDefault();
    }

    private async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        return await ExecuteAsync(conn, null, sql, parameters);
    }

    private static async Task<int> ExecuteAsync(MySqlConnection conn, MySqlTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<DriverOffer>> QueryAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        return await QueryAsync(conn, null, sql, parameters);
    }

    private static async Task<List<DriverOffer>> QueryAsync(
        MySqlConnection conn, MySqlTransaction? transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
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
                Status = reader.GetString(3) == "DriverCancelled" ? "Cancelled" : reader.GetString(3),
                CancelledBy = reader.GetString(3) == "DriverCancelled" ? "Driver" : reader.GetString(3) == "Cancelled" ? "Rider" : null,
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
