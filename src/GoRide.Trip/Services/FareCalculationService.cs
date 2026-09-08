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

            options.Add(new FareOption
            {
                VehicleTypeId = vehicleType.Id,
                VehicleTypeCode = vehicleType.Code,
                Available = vehicleType.Active,
                Fare = Math.Round(fare, 2),
                DistanceKm = Math.Round(distanceKm, 2),
                EstimatedDurationMinutes = Math.Round(durationMinutes, 1),
            });
        }

        return options;
    }

    // Haversine formula: great-circle (straight-line) distance between two lat/lng points.
    // Deliberately not real road distance — no routing provider exists yet, and for a
    // TukTuk-only, single-city fixed fare, this is a defensible, honest approximation.
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