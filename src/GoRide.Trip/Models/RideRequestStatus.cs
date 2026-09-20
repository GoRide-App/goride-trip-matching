namespace GoRide.Trip.Models;

/// <summary>Where a delivered ride request stands, from the rider's side.</summary>
public class RideRequestStatus
{
    public string TripId { get; set; } = string.Empty;

    /// <summary>"Searching" (an offer is still open), "Accepted", or "NoDriver" (every offer lapsed or failed).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The driver who accepted; only set when Status is "Accepted".</summary>
    public ActiveDriver? Driver { get; set; }
}
