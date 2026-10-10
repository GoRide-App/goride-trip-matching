using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using GoRide.Trip.Models;

namespace GoRide.Trip.Services;

public interface ITripCompletionNotifier
{
    /// <summary>
    /// Queues a just-completed trip to be reported to payment-service in the background. Returns
    /// false (after logging why) when it can't be: Payment:BaseUrl or InternalServices:ApiKey isn't
    /// configured, the trip has no fare, or the queue is full. Never waits and never throws.
    /// </summary>
    bool Enqueue(DriverOffer trip);
}

/// <summary>
/// Tells payment-service a trip was completed -- POST /internal/trip-events with its TRIP_COMPLETED
/// contract -- which is what creates the payment the rider then pays; without it riders could never
/// pay a real trip. Runs in the background so the driver's "Completed" step isn't held up, and
/// retries transient failures (network errors, timeouts, 5xx) with backoff. The eventId comes from
/// the trip, so payment-service recognises a retry as the same event rather than a second one.
/// </summary>
public class TripCompletionNotifier : BackgroundService, ITripCompletionNotifier
{
    public const string HttpClientName = "payment";

    private const int Capacity = 1000;

    private readonly Channel<DriverOffer> _queue = Channel.CreateBounded<DriverOffer>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TripCompletionNotifier> _logger;

    public TripCompletionNotifier(IHttpClientFactory httpClientFactory, IConfiguration configuration,
        ILogger<TripCompletionNotifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Waits before each retry; one attempt more than there are delays. Settable for tests.</summary>
    public TimeSpan[] RetryDelays { get; init; } =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) };

    private string? BaseUrl => _configuration["Payment:BaseUrl"];
    private string? ApiKey => _configuration["InternalServices:ApiKey"];

    public bool Enqueue(DriverOffer trip)
    {
        if (!CanReport(trip))
            return false;
        if (!_queue.Writer.TryWrite(trip))
        {
            _logger.LogError("Payment notification queue is full; payment-service won't hear that trip {TripId} completed.", trip.TripId);
            return false;
        }
        return true;
    }

    private bool CanReport(DriverOffer trip)
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            _logger.LogWarning("Payment:BaseUrl isn't configured, so payment-service won't hear that trip {TripId} completed.", trip.TripId);
            return false;
        }
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            _logger.LogWarning("InternalServices:ApiKey isn't configured, so payment-service won't hear that trip {TripId} completed.", trip.TripId);
            return false;
        }
        if (trip.Fare is null)
        {
            _logger.LogWarning("Trip {TripId} completed without a stored fare, so there is nothing for payment-service to charge; not reporting it.", trip.TripId);
            return false;
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var trip in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await DeliverAsync(trip, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Shutting down before payment-service was told that trip {TripId} completed.", trip.TripId);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error telling payment-service that trip {TripId} completed.", trip.TripId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Name whatever never got through, so it can be reconciled by hand.
            while (_queue.Reader.TryRead(out var trip))
                _logger.LogWarning("Shutting down before payment-service was told that trip {TripId} completed.", trip.TripId);
        }
    }

    private enum Attempt { Delivered, Retry, GiveUp }

    /// <summary>Sends the TRIP_COMPLETED event, retrying transient failures. True once payment-service accepted it.</summary>
    public async Task<bool> DeliverAsync(DriverOffer trip, CancellationToken cancellationToken)
    {
        if (!CanReport(trip))
            return false;

        for (var attempt = 0; ; attempt++)
        {
            var result = await SendOnceAsync(trip, cancellationToken);
            if (result != Attempt.Retry)
                return result == Attempt.Delivered;

            if (attempt >= RetryDelays.Length)
            {
                _logger.LogError("Gave up telling payment-service that trip {TripId} completed after {Attempts} attempts; the rider can't pay until it is reconciled.",
                    trip.TripId, attempt + 1);
                return false;
            }
            await Task.Delay(RetryDelays[attempt], cancellationToken);
        }
    }

    private async Task<Attempt> SendOnceAsync(DriverOffer trip, CancellationToken cancellationToken)
    {
        var fare = trip.Fare!.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl!.TrimEnd('/')}/internal/trip-events")
        {
            // payment-service's TRIP_COMPLETED contract. The trip only stores the fare quoted at request
            // time, so that is both the estimate and the final fare.
            Content = JsonContent.Create(new
            {
                eventId = $"trip-completed-{trip.TripId}",
                eventType = "TRIP_COMPLETED",
                tripId = trip.TripId,
                riderId = trip.RiderId,
                driverId = trip.DriverId,
                occurredAt = DateTime.SpecifyKind(trip.CompletedAt ?? DateTime.UtcNow, DateTimeKind.Utc),
                payload = new { finalFare = fare, estimatedFare = fare },
            }),
        };
        request.Headers.Add("X-Internal-Api-Key", ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Couldn't connect, or HttpClient's own timeout (reported as a TaskCanceledException).
            _logger.LogWarning(ex, "Could not reach payment-service to report trip {TripId} completed.", trip.TripId);
            return Attempt.Retry;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("payment-service accepted TRIP_COMPLETED for trip {TripId}.", trip.TripId);
                return Attempt.Delivered;
            }

            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
            if (body.Length > 500) body = body[..500] + "...";   // enough to see payment-service's error code
            var status = (int)response.StatusCode;
            if (status >= 500)
            {
                _logger.LogWarning("payment-service returned HTTP {StatusCode} for trip {TripId}'s TRIP_COMPLETED: {Body}",
                    status, trip.TripId, body);
                return Attempt.Retry;
            }
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // e.g. the trip already belongs to other participants, or this eventId was already used with
                // different data. Sending it again won't change the answer.
                _logger.LogWarning("payment-service reported a conflict for trip {TripId}'s TRIP_COMPLETED: {Body}", trip.TripId, body);
                return Attempt.GiveUp;
            }

            _logger.LogError("payment-service rejected TRIP_COMPLETED for trip {TripId} with HTTP {StatusCode}: {Body}",
                trip.TripId, status, body);
            return Attempt.GiveUp;
        }
    }
}
