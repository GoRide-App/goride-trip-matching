using GoRide.Trip.Models;

namespace GoRide.Trip.Data;

public interface IVehicleTypeRepository
{
    Task<List<VehicleType>> GetAllAsync();
}

public class VehicleTypeRepository : IVehicleTypeRepository
{
    private readonly IDbConnectionFactory _dbFactory;

    public VehicleTypeRepository(IDbConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<List<VehicleType>> GetAllAsync()
    {
        var results = new List<VehicleType>();

        await using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, code, capacity, base_fare, rate_per_km, rate_per_min, active
            FROM vehicle_types
            ORDER BY active DESC, base_fare ASC";

        await using var reader = await cmd.ExecuteReaderAsync();

        int idOrdinal = reader.GetOrdinal("id");
        int codeOrdinal = reader.GetOrdinal("code");
        int capacityOrdinal = reader.GetOrdinal("capacity");
        int baseFareOrdinal = reader.GetOrdinal("base_fare");
        int ratePerKmOrdinal = reader.GetOrdinal("rate_per_km");
        int ratePerMinOrdinal = reader.GetOrdinal("rate_per_min");
        int activeOrdinal = reader.GetOrdinal("active");

        while (await reader.ReadAsync())
        {
            results.Add(new VehicleType
            {
                Id = reader.GetValue(idOrdinal).ToString() ?? string.Empty,
                Code = reader.GetString(codeOrdinal),
                Capacity = reader.GetInt32(capacityOrdinal),
                BaseFare = reader.GetDecimal(baseFareOrdinal),
                RatePerKm = reader.GetDecimal(ratePerKmOrdinal),
                RatePerMin = reader.GetDecimal(ratePerMinOrdinal),
                Active = reader.GetBoolean(activeOrdinal),
            });
        }

        return results;
    }
}