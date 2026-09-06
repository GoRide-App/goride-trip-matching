using MySqlConnector;

namespace GoRide.Trip.Data;

/// <summary>
/// Every piece of data-access code in this service should ask for this interface
/// (via constructor injection) rather than building a MySqlConnection by hand.
/// Keeps the connection string assembly logic in exactly one place.
/// </summary>
public interface IDbConnectionFactory
{
    MySqlConnection CreateConnection();
}
