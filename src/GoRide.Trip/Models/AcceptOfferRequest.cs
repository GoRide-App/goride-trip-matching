namespace GoRide.Trip.Models;

/// <summary>Body of POST /matching/offers/{tripId}/accept.</summary>
public class AcceptOfferRequest
{
    public string DriverId { get; set; } = string.Empty;
}
