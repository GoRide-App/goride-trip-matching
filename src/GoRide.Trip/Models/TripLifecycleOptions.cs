namespace GoRide.Trip.Models;

public class TripLifecycleOptions
{
    public const string SectionName = "TripLifecycle";
    public int StaleAfterMinutes { get; set; } = 120;
}
