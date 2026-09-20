namespace GoRide.Trip.Models;

/// <summary>A ride request offered to one driver (a row of driver_offers).</summary>
public class DriverOffer
{
    public string TripId { get; set; } = string.Empty;
    public string DriverId { get; set; } = string.Empty;
    public string RiderId { get; set; } = string.Empty;

    /// <summary>Pending, Accepted, Declined or Expired.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Straight-line distance from the driver to the pickup, in km, when the offer was made.</summary>
    public double DistanceKm { get; set; }

    public string? PickupLocation { get; set; }
    public string? DropoffLocation { get; set; }
    public decimal? Fare { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When a Pending offer stops being acceptable (CreatedAt + the offer TTL).</summary>
    public DateTime ExpiresAt { get; set; }
}
