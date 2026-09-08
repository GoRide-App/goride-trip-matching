namespace GoRide.Trip.Models;

public class FareOption
{
    public string VehicleTypeId { get; set; } = string.Empty;
    public string VehicleTypeCode { get; set; } = string.Empty;
    /// <summary>Human-readable display name, e.g. "Tuk Tuk" for the TUKTUK code.</summary>
    public string DisplayName { get; set; } = string.Empty;
    public bool Available { get; set; }
    public decimal Fare { get; set; }
    public double DistanceKm { get; set; }
    public double EstimatedDurationMinutes { get; set; }
}

