using GoRide.Trip.Data;
using GoRide.Trip.Events;
using GoRide.Trip.Models;
using GoRide.Trip.Services;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

// ---- Controllers + Swagger ----
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddScoped<IVehicleTypeRepository, VehicleTypeRepository>();
builder.Services.AddScoped<IFareCalculationService, FareCalculationService>();
builder.Services.AddScoped<IDriverMatchingService, DriverMatchingService>();
builder.Services.AddScoped<IDriverOfferRepository, DriverOfferRepository>();
builder.Services.AddScoped<IRideRequestService, RideRequestService>();
builder.Services.AddScoped<IDriverOfferService, DriverOfferService>();
builder.Services.AddScoped<IDriverProfileLookup, DriverProfileLookup>();
builder.Services.AddMemoryCache();  // Driver vehicle details, so accepts and rider polls don't refetch every driver from identity-auth.
builder.Services.Configure<MatchingOptions>(builder.Configuration.GetSection(MatchingOptions.SectionName));
builder.Services.AddOptions<TripLifecycleOptions>()
    .Bind(builder.Configuration.GetSection(TripLifecycleOptions.SectionName))
    .Validate(o => o.StaleAfterMinutes > 0, "TripLifecycle:StaleAfterMinutes must be positive.")
    .ValidateOnStart();

// ---- Database (ADO.NET connection factory — see Data/MySqlConnectionFactory.cs) ----
builder.Services.AddScoped<IDbConnectionFactory, MySqlConnectionFactory>();

// ---- identity-auth client (for listing active drivers — see Services/ActiveDriversService.cs) ----
builder.Services.AddHttpClient<IActiveDriversService, ActiveDriversService>(client =>
{
    var identityAuthUrl = builder.Configuration["IdentityAuth:BaseUrl"]
        ?? throw new InvalidOperationException("Missing configuration: IdentityAuth:BaseUrl");
    client.BaseAddress = new Uri(identityAuthUrl);
    // The default is 100s; a hung identity-auth shouldn't hold a ride request that long. Its first
    // driver lookup after a restart (cold start against Azure MySQL) can take over 5s, so allow 15s.
    client.Timeout = TimeSpan.FromSeconds(15);
});

// ---- goride-location client (real road-network distance/duration — see Services/LocationClient.cs) ----
builder.Services.AddHttpClient<ILocationClient, LocationClient>(client =>
{
    var locationUrl = builder.Configuration["Location:BaseUrl"]
        ?? throw new InvalidOperationException("Missing configuration: Location:BaseUrl");
    client.BaseAddress = new Uri(locationUrl);
    client.Timeout = TimeSpan.FromSeconds(8);
});

// ---- payment-service: told when a trip completes, in the background (see Services/TripCompletionNotifier.cs) ----
builder.Services.AddHttpClient(TripCompletionNotifier.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<TripCompletionNotifier>();
builder.Services.AddSingleton<ITripCompletionNotifier>(sp => sp.GetRequiredService<TripCompletionNotifier>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TripCompletionNotifier>());

// ---- Kafka producer service (singleton) ----
builder.Services.AddSingleton<IKafkaProducerService, KafkaProducerService>();  // One shared Kafka connection for the whole app's lifetime, not a new one per request.
// Request handlers only queue trip events (TripEventPublisher); TripEventSender sends them to the
// topic goride-notification consumes, in the background, so no request ever waits on the broker.
builder.Services.AddSingleton<TripEventPublisher>();
builder.Services.AddSingleton<ITripEventPublisher>(sp => sp.GetRequiredService<TripEventPublisher>());
builder.Services.AddHostedService<TripEventSender>();

// ---- CORS: allow the Next.js frontend (local dev + Vercel-hosted) to call this API ----
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// ---- Try to ensure driver_offers exists (no migration tool on this service — see
// create_driver_offers.sql, which is the real source of the schema on the shared Azure DB).
// The trip_svc account has no CREATE grant, so this fails for EVERY developer on EVERY
// startup once the table already exists -- that's expected, not a regression, and it does
// not stop the app: it just means the table was created by that script, not by this check.
using (var scope = app.Services.CreateScope())
{
    try
    {
        await scope.ServiceProvider.GetRequiredService<IDriverOfferRepository>().EnsureSchemaAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogInformation(
            "Skipping automatic driver_offers setup ({Message}). This is expected: trip_svc can't create tables, " +
            "so driver_offers is created once via create_driver_offers.sql instead. The app is continuing normally.",
            ex.Message);
    }
}

// ---- Swagger UI (dev only — don't expose this publicly in production) ----
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors("FrontendPolicy");
app.UseHttpMetrics();
app.UseAuthorization();
app.MapControllers();
app.MapMetrics();
app.Run();

// Exposes the generated Program class so integration tests can spin up this app
// in-memory via WebApplicationFactory<Program> later, without any extra setup.
public partial class Program { }
