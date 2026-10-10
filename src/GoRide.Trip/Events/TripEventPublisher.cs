using System.Threading.Channels;
using GoRide.Trip.Models;

namespace GoRide.Trip.Events;

public interface ITripEventPublisher
{
    /// <summary>
    /// Queues the event to be sent to Kafka in the background (see TripEventSender). Returns
    /// straight away -- it never waits for the broker and never throws.
    /// </summary>
    Task PublishAsync(TripEvent tripEvent);
}

/// <summary>
/// Hands trip events to TripEventSender through a bounded in-memory queue. Publishing used to
/// await the broker inside the HTTP request, so an unreachable Kafka (e.g. Event Hubs' TLS
/// handshake failing locally) held every ride request and accept for MessageTimeoutMs before
/// the response went out. Events are notifications on top of the real state in driver_offers,
/// so if the queue ever fills up, the newest event is dropped with a warning rather than making
/// a request wait.
/// </summary>
public class TripEventPublisher : ITripEventPublisher
{
    public const int DefaultCapacity = 1000;

    private readonly Channel<TripEvent> _queue;
    private readonly ILogger<TripEventPublisher> _logger;

    public TripEventPublisher(ILogger<TripEventPublisher> logger, int capacity = DefaultCapacity)
    {
        _logger = logger;
        // FullMode=Wait only matters for WriteAsync; TryWrite (used below) just returns false when full.
        _queue = Channel.CreateBounded<TripEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    /// <summary>Read by TripEventSender.</summary>
    public ChannelReader<TripEvent> Reader => _queue.Reader;

    public Task PublishAsync(TripEvent tripEvent)
    {
        if (!_queue.Writer.TryWrite(tripEvent))
            _logger.LogWarning("Trip event queue is full; dropping {EventType} for trip {TripId} (event {EventId}).",
                tripEvent.EventType, tripEvent.TripId, tripEvent.EventId);

        return Task.CompletedTask;
    }
}
