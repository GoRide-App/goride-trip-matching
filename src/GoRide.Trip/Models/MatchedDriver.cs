namespace GoRide.Trip.Models;

/// <summary>An available driver of the right vehicle type near a pickup point.</summary>
public class MatchedDriver
{
    public string DriverId { get; set; } = string.Empty;
    public string VehicleTypeCode { get; set; } = string.Empty;
    public string VehicleMake { get; set; } = string.Empty;
    public string VehicleModel { get; set; } = string.Empty;
    public string VehiclePlate { get; set; } = string.Empty;
    public double Lat { get; set; }
    public double Lng { get; set; }

    /// <summary>Straight-line distance from the pickup point, in km.</summary>
    public double DistanceKm { get; set; }
}
