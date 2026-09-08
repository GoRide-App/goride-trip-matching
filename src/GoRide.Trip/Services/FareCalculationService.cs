using GoRide.Trip.Data;
using GoRide.Trip.Models;

namespace GoRide.Trip.Services;

public interface IFareCalculationService
{
    Task<List<FareOption>> EstimateFaresAsync(decimal startLat, decimal startLng, decimal endLat, decimal endLng);
}

public class FareCalculationService : IFareCalculationService
{
    private const double EarthRadiusKm = 6371.0;
    private const double AverageSpeedKmh = 25.0; // assumed average TukTuk speed in mixed city/suburban traffic

    private readonly IVehicleTypeRepository _vehicleTypeRepository;

    private static readonly (string Id, string Code, decimal BaseFare, decimal RatePerKm, decimal RatePerMin)[] AdditionalDisplayTypes = new[]
    {
        ("vt_car", "CAR", 300m, 120m, 10m),
        ("vt_bike", "BIKE", 100m, 50m, 5m),
        ("vt_xl", "XL", 500m, 180m, 15m),
    };

    public FareCalculationService(IVehicleTypeRepository vehicleTypeRepository)
    {
        _vehicleTypeRepository = vehicleTypeRepository;
    }

    public async Task<List<FareOption>> EstimateFaresAsync(decimal startLat, decimal startLng, decimal endLat, decimal endLng)
    {
        double distanceKm = CalculateDistanceKm((double)startLat, (double)startLng, (double)endLat, (double)endLng);
        double durationMinutes = (distanceKm / AverageSpeedKmh) * 60.0;

        var vehicleTypes = await _vehicleTypeRepository.GetAllAsync();
        var options = new List<FareOption>();

        foreach (var vehicleType in vehicleTypes)
        {
            decimal fare = vehicleType.BaseFare
                + ((decimal)distanceKm * vehicleType.RatePerKm)
                + ((decimal)durationMinutes * vehicleType.RatePerMin);

            bool isTukTuk = string.Equals(vehicleType.Code, "TUKTUK", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(vehicleType.Code, "TUK", StringComparison.OrdinalIgnoreCase);

            options.Add(new FareOption
            {
                VehicleTypeId = vehicleType.Id,
                VehicleTypeCode = vehicleType.Code,
                DisplayName = GetDisplayName(vehicleType.Code),
                // Only TUKTUK is allowed to be selected by riders in this stage
                Available = isTukTuk && vehicleType.Active,
                Fare = Math.Round(fare, 2),
                DistanceKm = Math.Round(distanceKm, 2),
                EstimatedDurationMinutes = Math.Round(durationMinutes, 1),
            });
        }

        // If other vehicle types (CAR, BIKE, XL) are not in the database yet, add them for display only
        foreach (var fallback in AdditionalDisplayTypes)
        {
            if (!options.Any(o => string.Equals(o.VehicleTypeCode, fallback.Code, StringComparison.OrdinalIgnoreCase)))
            {
                decimal fare = fallback.BaseFare
                    + ((decimal)distanceKm * fallback.RatePerKm)
                    + ((decimal)durationMinutes * fallback.RatePerMin);

                options.Add(new FareOption
                {
                    VehicleTypeId = fallback.Id,
                    VehicleTypeCode = fallback.Code,
                    DisplayName = GetDisplayName(fallback.Code),
                    Available = false, // Display only, cannot be selected by the rider
                    Fare = Math.Round(fare, 2),
                    DistanceKm = Math.Round(distanceKm, 2),
                    EstimatedDurationMinutes = Math.Round(durationMinutes, 1),
                });
            }
        }

        return options;
    }

    /// <summary>
    /// Maps internal vehicle type codes to human-readable display names shown in the frontend.
    /// TUKTUK is mapped to "Tuk Tuk". Others are mapped to their respective display names.
    /// No database or remote connection changes are made here; this is pure application logic.
    /// </summary>
    private static string GetDisplayName(string code) => code.ToUpperInvariant() switch
    {
        "TUKTUK" => "Tuk Tuk",
        "TUK"    => "Tuk Tuk",
        "CAR"    => "Car",
        "BIKE"   => "Bike",
        "XL"     => "XL",
        _        => code,
    };

    // Haversine formula: great-circle (straight-line) distance between two lat/lng points.
    private static double CalculateDistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        double dLat = ToRadians(lat2 - lat1);
        double dLng = ToRadians(lng2 - lng1);

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
            * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);

        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusKm * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}