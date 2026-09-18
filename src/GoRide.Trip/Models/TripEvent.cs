using System.Text.Json.Serialization;

namespace GoRide.Trip.Models;

/// <summary>
/// A trip domain event published to Kafka. Mirrors goride-notification's TripEvent
/// (Models/Events/TripEvent.cs) field-for-field — that service's consumer
/// deserializes this exact JSON, so keep the names in sync.
/// </summary>
public class TripEvent
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>e.g. "RIDE_REQUESTED", "DRIVER_ACCEPTED".</summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("tripId")]
    public string TripId { get; set; } = string.Empty;

    [JsonPropertyName("riderId")]
    public string RiderId { get; set; } = string.Empty;

    [JsonPropertyName("driverId")]
    public string? DriverId { get; set; }

    [JsonPropertyName("occurredAt")]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("payload")]
    public TripEventPayload Payload { get; set; } = new();
}

public class TripEventPayload
{
    [JsonPropertyName("driverName")]
    public string? DriverName { get; set; }

    [JsonPropertyName("vehiclePlate")]
    public string? VehiclePlate { get; set; }

    [JsonPropertyName("vehicleType")]
    public string? VehicleType { get; set; }

    [JsonPropertyName("etaMinutes")]
    public int? EtaMinutes { get; set; }

    [JsonPropertyName("fare")]
    public decimal? Fare { get; set; }

    [JsonPropertyName("pickupLocation")]
    public string? PickupLocation { get; set; }

    [JsonPropertyName("dropoffLocation")]
    public string? DropoffLocation { get; set; }

    [JsonPropertyName("changeReason")]
    public string? ChangeReason { get; set; }
}
