using System.ComponentModel.DataAnnotations;

namespace GoRide.Trip.Models;

public class DriverDecisionRequest
{
    [Required, StringLength(64)]
    public string DriverId { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Reason { get; set; }
}

public class CancelRideRequest
{
    [Required, StringLength(64)]
    public string RiderId { get; set; } = string.Empty;
}
