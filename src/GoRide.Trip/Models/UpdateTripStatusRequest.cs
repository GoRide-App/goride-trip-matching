using System.ComponentModel.DataAnnotations;

namespace GoRide.Trip.Models;

public class UpdateTripStatusRequest
{
    [Required]
    [StringLength(64)]
    public string DriverId { get; set; } = string.Empty;

    /// <summary>"Arrived", "InProgress", or "Completed" -- one step at a time, in that order.</summary>
    [Required]
    [RegularExpression("^(Arrived|InProgress|Completed)$", ErrorMessage = "action must be one of: Arrived, InProgress, Completed.")]
    public string Action { get; set; } = string.Empty;
}
