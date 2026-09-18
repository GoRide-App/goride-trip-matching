namespace GoRide.Trip.Models;

public class FindNearbyDriversRequest
{
    public double PickupLat { get; set; }
    public double PickupLng { get; set; }
    public string VehicleTypeCode { get; set; } = string.Empty;
}
