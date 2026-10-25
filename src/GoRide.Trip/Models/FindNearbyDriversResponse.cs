namespace GoRide.Trip.Models;

public class FindNearbyDriversResponse
{
    /// <summary>False when no available driver of the requested type was found within the radius.</summary>
    public bool Matched { get; set; }

    /// <summary>The single fixed search radius that was used.</summary>
    public double RadiusKm { get; set; }

    /// <summary>Matches, nearest first.</summary>
    public List<MatchedDriver> Drivers { get; set; } = new();
}
