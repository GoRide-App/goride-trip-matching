using GoRide.Trip.Data;
using GoRide.Trip.Events;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

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
builder.Services.Configure<MatchingOptions>(builder.Configuration.GetSection(MatchingOptions.SectionName));

// ---- Database (ADO.NET connection factory — see Data/MySqlConnectionFactory.cs) ----
builder.Services.AddScoped<IDbConnectionFactory, MySqlConnectionFactory>();

// ---- identity-auth client (for listing active drivers — see Services/ActiveDriversService.cs) ----
builder.Services.AddHttpClient<IActiveDriversService, ActiveDriversService>(client =>
{
    var identityAuthUrl = builder.Configuration["IdentityAuth:BaseUrl"]
        ?? throw new InvalidOperationException("Missing configuration: IdentityAuth:BaseUrl");
    client.BaseAddress = new Uri(identityAuthUrl);
});

// ---- goride-location client (real road-network distance/duration — see Services/LocationClient.cs) ----
builder.Services.AddHttpClient<ILocationClient, LocationClient>(client =>
{
    var locationUrl = builder.Configuration["Location:BaseUrl"]
        ?? throw new InvalidOperationException("Missing configuration: Location:BaseUrl");
    client.BaseAddress = new Uri(locationUrl);
    client.Timeout = TimeSpan.FromSeconds(8);
});

// ---- Kafka producer service (singleton) ----
builder.Services.AddSingleton<KafkaProducerService>();  // One shared Kafka connection for the whole app's lifetime, not a new one per request.
builder.Services.AddSingleton<ITripEventPublisher, TripEventPublisher>();  // Publishes trip events to the topic goride-notification consumes.

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

// ---- Ensure driver_offers exists (no migration tool on this service — schema is created
// idempotently on startup). A failure is logged but doesn't stop the app: fare estimates
// and the plain driver search don't need this table.
using (var scope = app.Services.CreateScope())
{
    try
    {
        await scope.ServiceProvider.GetRequiredService<IDriverOfferRepository>().EnsureSchemaAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to ensure driver_offers schema exists.");
    }
}

// ---- Swagger UI (dev only — don't expose this publicly in production) ----
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("FrontendPolicy");
app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposes the generated Program class so integration tests can spin up this app
// in-memory via WebApplicationFactory<Program> later, without any extra setup.
public partial class Program { }
