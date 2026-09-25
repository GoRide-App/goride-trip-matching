namespace GoRide.Trip.Models;

public class UpdateTripStatusRequest
{
    public string DriverId { get; set; } = string.Empty;

    /// <summary>"Arrived", "InProgress", or "Completed" -- one step at a time, in that order.</summary>
    public string Action { get; set; } = string.Empty;
}
