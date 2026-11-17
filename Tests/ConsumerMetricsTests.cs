using System.Diagnostics.Metrics;
using CoinTracker.Shared.Observability;
using Xunit;

namespace CoinTracker.Tests;

public sealed class ConsumerMetricsTests
{
    [Fact]
    public void CountersSeparateSuccessesAndFailedAttemptsWithoutDoubleCountingRetries()
    {
        var queue = $"counters-{Guid.NewGuid()}";
        var measurements = new Dictionary<string, long>();
        using var listener = Listen(queue, (name, value) =>
            measurements[name] = measurements.GetValueOrDefault(name) + value);

        CoinTrackerMetrics.TrackConsumer(queue, () => false);
        Assert.Equal(0, measurements["cointracker.consumer.messages.processed"]);
        Assert.Equal(0, measurements["cointracker.consumer.processing.failures"]);

        CoinTrackerMetrics.EventHandled(queue, "failed");
        CoinTrackerMetrics.EventHandled(queue, "retrying");
        CoinTrackerMetrics.EventHandled(queue, "failed");
        CoinTrackerMetrics.EventHandled(queue, "retrying");
        CoinTrackerMetrics.EventHandled(queue, "succeeded");
        CoinTrackerMetrics.EventHandled(queue, "succeeded");

        Assert.Equal(2, measurements["cointracker.consumer.messages.processed"]);
        Assert.Equal(2, measurements["cointracker.consumer.processing.failures"]);
    }

    [Fact]
    public void GaugeReflectsSubscriptionStateAndReplacementWithoutAccumulating()
    {
        var queue = $"gauge-{Guid.NewGuid()}";
        long active = -1;
        using var listener = Listen(queue, (name, value) =>
        {
            if (name == "cointracker.consumer.active") active = value;
        });
        var registered = false;
        var channelOpen = true;
        CoinTrackerMetrics.TrackConsumer(queue, () => registered && channelOpen);
        listener.RecordObservableInstruments();
        Assert.Equal(0, active);

        registered = true;
        listener.RecordObservableInstruments();
        listener.RecordObservableInstruments();
        Assert.Equal(1, active);

        channelOpen = false;
        listener.RecordObservableInstruments();
        Assert.Equal(0, active);

        channelOpen = true;
        listener.RecordObservableInstruments();
        Assert.Equal(1, active);

        registered = false;
        listener.RecordObservableInstruments();
        Assert.Equal(0, active);

        CoinTrackerMetrics.TrackConsumer(queue, () => true);
        listener.RecordObservableInstruments();
        Assert.Equal(1, active);
    }

    private static MeterListener Listen(string queue, Action<string, long> record)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == CoinTrackerMetrics.ConsumerMeterName)
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "queue" && Equals(tag.Value, queue))
                    record(instrument.Name, value);
        });
        listener.Start();
        return listener;
    }
}
