namespace GoRide.Trip.Models;

/// <summary>Where a delivered ride request stands, from the rider's side.</summary>
public class RideRequestStatus
{
    public string TripId { get; set; } = string.Empty;

    /// <summary>
    /// "Searching" (an offer is still open), "NoDriver" (every offer lapsed or failed), or one of
    /// the accepted driver's trip stages: "Accepted", "Arrived", "InProgress", "Completed".
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The driver who accepted; only set once Status is "Accepted" or later.</summary>
    public ActiveDriver? Driver { get; set; }

    // Trip details, present once Status is "Accepted" or later -- the rider's device polls this
    // to learn what the accepted offer actually is (SCRUM-58/59 never sent it back), and the
    // driver's device uses it as the source of truth for the pickup/dropoff pins on their own map.
    public string? PickupLocation { get; set; }
    public double? PickupLat { get; set; }
    public double? PickupLng { get; set; }
    public string? DropoffLocation { get; set; }
    public double? DropoffLat { get; set; }
    public double? DropoffLng { get; set; }
    public decimal? Fare { get; set; }
}
