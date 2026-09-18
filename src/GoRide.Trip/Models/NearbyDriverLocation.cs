namespace GoRide.Trip.Models;

/// <summary>One available driver as returned by goride-location's GET /location/nearby-drivers.</summary>
public class NearbyDriverLocation
{
    public string DriverId { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double Heading { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}
