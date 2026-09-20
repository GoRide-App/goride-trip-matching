using GoRide.Trip.Models;

namespace GoRide.Trip.Events;

public interface ITripEventPublisher
{
    Task PublishAsync(TripEvent tripEvent);
}

/// <summary>
/// Publishes trip events to the topic goride-notification's consumer subscribes to.
/// Sits on top of KafkaProducerService so the topic name and message key live in
/// one place, and so callers can be unit-tested without a real Kafka producer.
/// </summary>
public class TripEventPublisher : ITripEventPublisher
{
    // Same default as goride-notification's TripEventConsumerService, so both
    // sides agree even when Kafka:TripEventsTopic isn't configured.
    private const string DefaultTopic = "goride.trip.events";

    private readonly KafkaProducerService _producer;
    private readonly string _topic;

    public TripEventPublisher(KafkaProducerService producer, IConfiguration config)
    {
        _producer = producer;
        _topic = config["Kafka:TripEventsTopic"] ?? DefaultTopic;
    }

    // Keyed by trip so all events for one trip land on one partition, in order.
    public Task PublishAsync(TripEvent tripEvent) =>
        _producer.PublishAsync(_topic, tripEvent.TripId, tripEvent);
}
