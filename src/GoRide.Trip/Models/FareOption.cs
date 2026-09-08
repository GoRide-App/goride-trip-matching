namespace GoRide.Trip.Models;

public class FareOption
{
    public string VehicleTypeId { get; set; } = string.Empty;
    public string VehicleTypeCode { get; set; } = string.Empty;
    public bool Available { get; set; }
    public decimal Fare { get; set; }
    public double DistanceKm { get; set; }
    public double EstimatedDurationMinutes { get; set; }
}

