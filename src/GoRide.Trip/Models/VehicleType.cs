namespace GoRide.Trip.Models;

public class VehicleType {
    public string Id {get; set;} = string.Empty;
    public string Code {get; set;} = string.Empty;
    public int Capacity {get; set;}
    public decimal BaseFare {get; set;}
    public decimal RatePerKm {get; set;}
    public decimal RatePerMin {get; set;}
    public bool Active {get; set;}
}