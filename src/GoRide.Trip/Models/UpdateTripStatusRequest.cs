using System.ComponentModel.DataAnnotations;

namespace GoRide.Trip.Models;

public class UpdateTripStatusRequest
{
    [Required]
    [StringLength(64)]
    public string DriverId { get; set; } = string.Empty;

    /// <summary>Next/current stage: Accepted (no-op only), Arrived, InProgress, or Completed.</summary>
    [Required]
    [RegularExpression("^(Accepted|Arrived|InProgress|Completed)$", ErrorMessage = "action must be one of: Accepted, Arrived, InProgress, Completed.")]
    public string Action { get; set; } = string.Empty;
}
