using GoRide.Trip.Models;

namespace GoRide.Trip.Events;

/// <summary>
/// Sends the events TripEventPublisher queued to the topic goride-notification's consumer
/// subscribes to, one at a time, in the background. A failed send is logged and the next event
/// goes out regardless -- nothing here is ever reported back to the request that raised the event.
/// </summary>
public class TripEventSender : BackgroundService
{
    // Same default as goride-notification's TripEventConsumerService, so both
    // sides agree even when Kafka:TripEventsTopic isn't configured.
    private const string DefaultTopic = "goride.trip.events";

    // How long shutdown waits for events still queued (or in flight) to reach the broker.
    private static readonly TimeSpan ShutdownFlushWindow = TimeSpan.FromSeconds(3);

    private readonly TripEventPublisher _queue;
    private readonly IKafkaProducerService _producer;
    private readonly string _topic;
    private readonly ILogger<TripEventSender> _logger;

    public TripEventSender(TripEventPublisher queue, IKafkaProducerService producer, IConfiguration config,
        ILogger<TripEventSender> logger)
    {
        _queue = queue;
        _producer = producer;
        _topic = config["Kafka:TripEventsTopic"] ?? DefaultTopic;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var tripEvent in _queue.Reader.ReadAllAsync(stoppingToken))
                await SendAsync(tripEvent, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping; StopAsync sends whatever is left.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        // Best effort: give what's still queued one short window to reach the broker (sent together,
        // not one after another), then let the producer finish anything already handed to it.
        var deadline = DateTime.UtcNow + ShutdownFlushWindow;
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(ShutdownFlushWindow);

        var remaining = new List<Task>();
        while (_queue.Reader.TryRead(out var tripEvent))
            remaining.Add(SendAsync(tripEvent, window.Token));
        await Task.WhenAll(remaining);

        try
        {
            var left = deadline - DateTime.UtcNow;
            var unsent = _producer.Flush(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            if (unsent > 0)
                _logger.LogWarning("{Count} trip event(s) had not reached Kafka by shutdown and were dropped.", unsent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not flush trip events to Kafka on shutdown.");
        }
    }

    // Keyed by trip so all events for one trip land on one partition, in order.
    private async Task SendAsync(TripEvent tripEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _producer.PublishAsync(_topic, tripEvent.TripId, tripEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Stopped waiting for Kafka to confirm {EventType} for trip {TripId} (event {EventId}) during shutdown; it may not have been delivered.",
                tripEvent.EventType, tripEvent.TripId, tripEvent.EventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not publish {EventType} for trip {TripId} (event {EventId}) to Kafka.",
                tripEvent.EventType, tripEvent.TripId, tripEvent.EventId);
        }
    }
}
