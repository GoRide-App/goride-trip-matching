namespace GoRide.Trip.Models;

public class FindNearbyDriversRequest
{
    public double PickupLat { get; set; }
    public double PickupLng { get; set; }
    public string VehicleTypeCode { get; set; } = string.Empty;

    // Trip context. Only needed by POST /matching/ride-requests (delivering the
    // request to the drivers found); the plain search ignores these.
    public string? TripId { get; set; }
    public string? RiderId { get; set; }
    public string? PickupLocation { get; set; }
    public string? DropoffLocation { get; set; }
    public decimal? Fare { get; set; }
}
