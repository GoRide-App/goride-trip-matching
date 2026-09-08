namespace GoRide.Trip.Models;

public class FareEstimateRequest
{
    public decimal? StartLat { get; set; }
    public decimal? StartLng { get; set; }
    public decimal? EndLat { get; set; }
    public decimal? EndLng { get; set; }
}

// Nullable decimal?, deliberately — lets us detect "field wasn't sent at all" and reject it, rather than silently treating a missing field as coordinate 0,0