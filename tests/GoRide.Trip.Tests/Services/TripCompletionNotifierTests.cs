using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using GoRide.Trip.Models;
using GoRide.Trip.Services;

namespace GoRide.Trip.Tests.Services;

/// <summary>
/// Pins the TRIP_COMPLETED call payment-service's POST /internal/trip-events accepts, and which
/// failures are retried. Without this call a rider can never pay a real trip.
/// </summary>
public class TripCompletionNotifierTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<int, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<int, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read the body now -- the notifier disposes the request once it has its answer.
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request, body));
            return await _respond(Requests.Count);
        }
    }

    private static Task<HttpResponseMessage> Status(HttpStatusCode status) =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"code\":\"TEST\"}", Encoding.UTF8, "application/json"),
        });

    private static TripCompletionNotifier Create(StubHandler handler, string? baseUrl = "http://payment.test/", string? apiKey = "super-secret")
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(TripCompletionNotifier.HttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payment:BaseUrl"] = baseUrl,
                ["InternalServices:ApiKey"] = apiKey,
            })
            .Build();
        return new TripCompletionNotifier(factory.Object, config, NullLogger<TripCompletionNotifier>.Instance)
        {
            RetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero },
        };
    }

    private static DriverOffer Trip(decimal? fare = 725.50m) => new()
    {
        TripId = "trip-1",
        DriverId = "d1",
        RiderId = "rider-sub-1",
        Status = "Completed",
        Fare = fare,
        CreatedAt = new DateTime(2026, 10, 9, 9, 50, 0, DateTimeKind.Utc),
        CompletedAt = new DateTime(2026, 10, 9, 10, 15, 30, 123, DateTimeKind.Utc),
    };

    [Fact]
    public async Task SendsTheTripCompletedContract_WithTheApiKeyHeader()
    {
        var handler = new StubHandler(_ => Status(HttpStatusCode.OK));

        Assert.True(await Create(handler).DeliverAsync(Trip(), CancellationToken.None));

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://payment.test/internal/trip-events", request.RequestUri!.ToString());
        Assert.Equal("super-secret", request.Headers.GetValues("X-Internal-Api-Key").Single());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("trip-completed-trip-1", root.GetProperty("eventId").GetString());
        Assert.Equal("TRIP_COMPLETED", root.GetProperty("eventType").GetString());
        Assert.Equal("trip-1", root.GetProperty("tripId").GetString());
        Assert.Equal("rider-sub-1", root.GetProperty("riderId").GetString());
        Assert.Equal("d1", root.GetProperty("driverId").GetString());
        Assert.Equal("2026-10-09T10:15:30.123Z", root.GetProperty("occurredAt").GetString());
        Assert.Equal(725.50m, root.GetProperty("payload").GetProperty("finalFare").GetDecimal());
        Assert.Equal(725.50m, root.GetProperty("payload").GetProperty("estimatedFare").GetDecimal());
        Assert.Contains("\"finalFare\":725.50", body);   // two decimal places, as stored
    }

    [Fact]
    public async Task TransientFailures_AreRetried_UntilPaymentAccepts()
    {
        var handler = new StubHandler(attempt => attempt switch
        {
            1 => Status(HttpStatusCode.ServiceUnavailable),
            2 => throw new HttpRequestException("connection refused"),
            _ => Status(HttpStatusCode.OK),
        });

        Assert.True(await Create(handler).DeliverAsync(Trip(), CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests.Select(r => r.Body).Distinct());   // the same event every time
    }

    [Fact]
    public async Task KeepsFailing_GivesUpAfterTheLastRetry()
    {
        var handler = new StubHandler(_ => Status(HttpStatusCode.InternalServerError));

        Assert.False(await Create(handler).DeliverAsync(Trip(), CancellationToken.None));
        Assert.Equal(4, handler.Requests.Count);   // the first attempt + one per retry delay
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task ClientErrors_AreNotRetried(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Status(status));

        Assert.False(await Create(handler).DeliverAsync(Trip(), CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(null, "super-secret", 725.50)]   // Payment:BaseUrl not configured
    [InlineData("http://payment.test", null, 725.50)]   // no API key to authenticate with
    [InlineData("http://payment.test", "super-secret", null)]   // nothing to charge
    public void Enqueue_SkipsWhatCannotBeReported(string? baseUrl, string? apiKey, double? fare)
    {
        var handler = new StubHandler(_ => Status(HttpStatusCode.OK));

        Assert.False(Create(handler, baseUrl, apiKey).Enqueue(Trip(fare is null ? null : (decimal)fare)));
    }

    [Fact]
    public async Task Enqueue_ReturnsImmediately_AndTheCallHappensInTheBackground()
    {
        var payment = new TaskCompletionSource<HttpResponseMessage>();   // payment-service is slow to answer
        var handler = new StubHandler(_ => payment.Task);
        var notifier = Create(handler);
        await notifier.StartAsync(CancellationToken.None);

        Assert.True(notifier.Enqueue(Trip()));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.Requests.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Single(handler.Requests);

        payment.SetResult(new HttpResponseMessage(HttpStatusCode.OK));
        await notifier.StopAsync(CancellationToken.None);
    }
}
