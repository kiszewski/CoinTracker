using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CoinTracker.Shared.Observability;

public static class CoinTrackerMetrics
{
    public const string ConsumerMeterName = "CoinTracker.Consumers";
    private static readonly Meter Meter = new("CoinTracker", "1.0.0");
    private static readonly Meter ConsumerMeter = new(ConsumerMeterName, "1.0.0");
    private static readonly ConcurrentDictionary<string, long> QueueDepths = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Func<bool>> Consumers = new(StringComparer.Ordinal);

    private static readonly Counter<long> MessagesProcessed = ConsumerMeter.CreateCounter<long>(
        "cointracker.consumer.messages.processed", description: "Messages successfully handled and acknowledged; includes successful redeliveries.");
    private static readonly Counter<long> ProcessingFailures = ConsumerMeter.CreateCounter<long>(
        "cointracker.consumer.processing.failures", description: "Failed processing attempts, including retries and malformed messages; excludes shutdown cancellation.");
    private static readonly ObservableGauge<long> ActiveConsumers = ConsumerMeter.CreateObservableGauge(
        "cointracker.consumer.active", ObserveActiveConsumers, description: "Active RabbitMQ subscriptions per queue in this application instance.");

    private static readonly Counter<long> IngestionRuns = Meter.CreateCounter<long>("cointracker.ingestion.runs", "runs");
    private static readonly Counter<long> SnapshotsStored = Meter.CreateCounter<long>("cointracker.snapshots.stored", "snapshots");
    private static readonly Counter<long> EventsPublished = Meter.CreateCounter<long>("cointracker.events.published", "events");
    private static readonly Counter<long> EventHandling = Meter.CreateCounter<long>("cointracker.events.handled", "events");
    private static readonly Counter<long> ReportsCreated = Meter.CreateCounter<long>("cointracker.reports.created", "reports");
    private static readonly Counter<long> Deliveries = Meter.CreateCounter<long>("cointracker.deliveries", "deliveries");
    private static readonly Counter<long> DeliveryRetries = Meter.CreateCounter<long>("cointracker.delivery.retries", "retries");

    private static readonly object GaugeRegistration = Meter.CreateObservableGauge(
        "cointracker.rabbitmq.queue.messages", ObserveQueueDepths, "messages");

    public static void IngestionSucceeded(int snapshotCount)
    {
        IngestionRuns.Add(1, new KeyValuePair<string, object?>("outcome", "succeeded"));
        SnapshotsStored.Add(snapshotCount);
    }

    public static void IngestionFailed() => IngestionRuns.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
    public static void EventPublished(string eventType) => EventsPublished.Add(1, new KeyValuePair<string, object?>("event_type", eventType));
    public static void EventHandled(string consumer, string outcome)
    {
        EventHandling.Add(1, new KeyValuePair<string, object?>("consumer", consumer), new KeyValuePair<string, object?>("outcome", outcome));
        var queue = new KeyValuePair<string, object?>("queue", consumer);
        if (outcome == "succeeded") MessagesProcessed.Add(1, queue);
        else if (outcome == "failed") ProcessingFailures.Add(1, queue);
    }

    public static void TrackConsumer(string queue, Func<bool> isActive)
    {
        Consumers[queue] = isActive;
        var tag = new KeyValuePair<string, object?>("queue", queue);
        MessagesProcessed.Add(0, tag);
        ProcessingFailures.Add(0, tag);
    }
    public static void ReportCreated(string reportType, int count = 1) => ReportsCreated.Add(count, new KeyValuePair<string, object?>("report_type", reportType));
    public static void DeliveryCompleted(string channel, string outcome) => Deliveries.Add(1,
        new KeyValuePair<string, object?>("channel", channel), new KeyValuePair<string, object?>("outcome", outcome));
    public static void DeliveryRetried(string channel) => DeliveryRetries.Add(1, new KeyValuePair<string, object?>("channel", channel));
    public static void SetRabbitQueueDepth(string queue, long depth) => QueueDepths[queue] = depth;

    private static IEnumerable<Measurement<long>> ObserveQueueDepths()
    {
        foreach (var (queue, depth) in QueueDepths)
            yield return new Measurement<long>(depth, new KeyValuePair<string, object?>("queue", queue));
    }

    private static IEnumerable<Measurement<long>> ObserveActiveConsumers()
    {
        foreach (var (queue, isActive) in Consumers)
            yield return new Measurement<long>(isActive() ? 1 : 0, new KeyValuePair<string, object?>("queue", queue));
    }
}
