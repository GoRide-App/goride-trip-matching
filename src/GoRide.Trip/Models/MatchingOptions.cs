namespace GoRide.Trip.Models;

/// <summary>Bound from the "Matching" section of appsettings.json.</summary>
public class MatchingOptions
{
    public const string SectionName = "Matching";

    /// <summary>The one fixed radius searched around the pickup point (no escalating rounds).</summary>
    public double RadiusKm { get; set; } = 5;

    /// <summary>Cap on how many nearest drivers a single request matches.</summary>
    public int MaxDrivers { get; set; } = 10;
}
