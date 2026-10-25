namespace GoRide.Trip.Models;

/// <summary>One driver as returned by identity-auth's active-drivers list.</summary>
public class ActiveDriver
{
    public string DriverId { get; set; } = string.Empty;
    public string VehicleTypeCode { get; set; } = string.Empty;
    public string VehicleMake { get; set; } = string.Empty;
    public string VehicleModel { get; set; } = string.Empty;
    public string VehiclePlate { get; set; } = string.Empty;
}
