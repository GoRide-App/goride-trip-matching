namespace GoRide.Trip.Models;

/// <summary>The only supported forward steps in an accepted trip's lifecycle.</summary>
public static class TripStatusFlow
{
    public static string? PreviousStatus(string? action) => action switch
    {
        "Arrived" => "Accepted",
        "InProgress" => "Arrived",
        "Completed" => "InProgress",
        _ => null,
    };

    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 64;
}
