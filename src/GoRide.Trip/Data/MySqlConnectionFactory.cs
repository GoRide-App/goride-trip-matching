using MySqlConnector;

namespace GoRide.Trip.Data;

/// <summary>
/// Builds the MySQL connection string from separate config keys (Db:Host, Db:Port, etc.)
/// rather than one pre-built string, so only the password needs to be a secret —
/// everything else is safe to commit in appsettings.json.
///
/// The password comes from appsettings.Development.json locally (gitignored), or from
/// the Db__Password environment variable in Docker/Azure. Both map onto the same
/// "Db:Password" configuration key automatically — that's a built-in ASP.NET Core
/// convention (double underscore = nested key separator), not something wired up here.
/// </summary>
public class MySqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public MySqlConnectionFactory(IConfiguration configuration)
    {
        var sslModeRaw = configuration["Db:SslMode"] ?? "Required";

        var csBuilder = new MySqlConnectionStringBuilder
        {
            Server = configuration["Db:Host"]
                ?? throw new InvalidOperationException("Missing configuration: Db:Host"),
            Port = uint.Parse(configuration["Db:Port"] ?? "3306"),
            Database = configuration["Db:Database"]
                ?? throw new InvalidOperationException("Missing configuration: Db:Database"),
            UserID = configuration["Db:User"]
                ?? throw new InvalidOperationException("Missing configuration: Db:User"),
            Password = configuration["Db:Password"]
                ?? throw new InvalidOperationException(
                    "Missing configuration: Db:Password (set it in appsettings.Development.json locally, " +
                    "or as the Db__Password environment variable in Docker/Azure)"),
            SslMode = Enum.Parse<MySqlSslMode>(sslModeRaw, ignoreCase: true),
        };

        _connectionString = csBuilder.ConnectionString;
    }

    public MySqlConnection CreateConnection() => new MySqlConnection(_connectionString);
}
