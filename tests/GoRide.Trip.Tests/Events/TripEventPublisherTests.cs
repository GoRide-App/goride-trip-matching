using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GoRide.Trip.Events;
using GoRide.Trip.Models;

namespace GoRide.Trip.Tests.Events;

/// <summary>
/// Publishing used to await Kafka inside the HTTP request, so an unreachable broker held every
/// ride request and accept for MessageTimeoutMs. These pin that publishing only queues, and that
/// the background sender is what actually talks to the broker.
/// </summary>
public class TripEventPublisherTests
{
    private sealed class FakeProducer : IKafkaProducerService
    {
        public ConcurrentQueue<(string Topic, string Key, object? Payload)> Sent { get; } = new();
        public Func<object?, Task> Reply { get; set; } = _ => Task.CompletedTask;
        public int Flushes;

        public Task PublishAsync<T>(string topic, string key, T eventPayload, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue((topic, key, eventPayload));
            return Reply(eventPayload);
        }

        public int Flush(TimeSpan timeout)
        {
            Interlocked.Increment(ref Flushes);
            return 0;
        }
    }

    private static TripEvent Event(string tripId, string type = "RIDE_REQUESTED") =>
        new() { EventType = type, TripId = tripId, RiderId = "rider-1", DriverId = "d1" };

    private static TripEventSender Sender(TripEventPublisher queue, FakeProducer producer) =>
        new(queue, producer,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kafka:TripEventsTopic"] = "trip-events-test" })
                .Build(),
            NullLogger<TripEventSender>.Instance);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Publish_ReturnsImmediately_WhileTheBrokerHasNotAnswered()
    {
        var broker = new TaskCompletionSource();            // the broker never answers until we say so
        var producer = new FakeProducer { Reply = _ => broker.Task };
        var queue = new TripEventPublisher(NullLogger<TripEventPublisher>.Instance);
        var sender = Sender(queue, producer);
        await sender.StartAsync(CancellationToken.None);

        var publishes = new[] { Event("t1"), Event("t2"), Event("t3") }.Select(queue.PublishAsync).ToList();

        Assert.All(publishes, p => Assert.True(p.IsCompletedSuccessfully));
        await WaitUntil(() => producer.Sent.Count == 1);    // the first send is stuck waiting on the broker...
        Assert.Single(producer.Sent);                        // ...and the rest wait their turn, in the background

        broker.SetResult();
        await WaitUntil(() => producer.Sent.Count == 3);
        Assert.Equal(new[] { "t1", "t2", "t3" }, producer.Sent.Select(s => s.Key));   // keyed by trip, in order
        Assert.All(producer.Sent, s => Assert.Equal("trip-events-test", s.Topic));

        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueueFull_DropsTheNewestEvent_WithoutThrowingOrWaiting()
    {
        var queue = new TripEventPublisher(NullLogger<TripEventPublisher>.Instance, capacity: 2);

        await queue.PublishAsync(Event("t1"));
        await queue.PublishAsync(Event("t2"));
        var third = queue.PublishAsync(Event("t3"));

        Assert.True(third.IsCompletedSuccessfully);
        Assert.True(queue.Reader.TryRead(out var first));
        Assert.True(queue.Reader.TryRead(out var second));
        Assert.False(queue.Reader.TryRead(out _));
        Assert.Equal(new[] { "t1", "t2" }, new[] { first!.TripId, second!.TripId });
    }

    [Fact]
    public async Task FailedSend_IsLogged_AndTheNextEventStillGoesOut()
    {
        var producer = new FakeProducer
        {
            Reply = e => ((TripEvent)e!).TripId == "t1"
                ? Task.FromException(new Exception("broker unreachable"))
                : Task.CompletedTask,
        };
        var queue = new TripEventPublisher(NullLogger<TripEventPublisher>.Instance);
        var sender = Sender(queue, producer);
        await sender.StartAsync(CancellationToken.None);

        await queue.PublishAsync(Event("t1"));
        await queue.PublishAsync(Event("t2"));

        await WaitUntil(() => producer.Sent.Count == 2);
        Assert.Equal(new[] { "t1", "t2" }, producer.Sent.Select(s => s.Key));

        await sender.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Shutdown_SendsWhatIsStillQueued_AndFlushesTheProducer()
    {
        var producer = new FakeProducer();
        var queue = new TripEventPublisher(NullLogger<TripEventPublisher>.Instance);
        await queue.PublishAsync(Event("t1"));
        await queue.PublishAsync(Event("t2", "DRIVER_ACCEPTED"));

        // Never started, so nothing was sent while running -- only the shutdown flush can send these.
        await Sender(queue, producer).StopAsync(CancellationToken.None);

        Assert.Equal(new[] { "t1", "t2" }, producer.Sent.Select(s => s.Key).OrderBy(k => k));
        Assert.Equal(1, producer.Flushes);
    }
}
