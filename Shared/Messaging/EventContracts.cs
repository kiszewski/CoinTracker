namespace CoinTracker.Shared.Messaging;

public static class EventTypes
{
    public const string MarketDataPopulated = "market-data.populated.v1";
    public const string ReportCreated = "report.created.v1";
}

public sealed record EventEnvelope<T>(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    DateTime OccurredAtUtc,
    Guid CorrelationId,
    Guid? CausationId,
    string Producer,
    T Payload);

public sealed record MarketDataPopulatedPayload(
    Guid IngestionRunId,
    string Provider,
    DateTime ObservedAtUtc,
    int AssetCount,
    int SnapshotCount);

public sealed record ReportCreatedPayload(
    Guid ReportId,
    string ReportType,
    int RuleVersion,
    string CoinCode,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc);

public sealed record ReportCreatedIntegrationEvent(
    Guid EventId,
    Guid CorrelationId,
    Guid CausationId,
    ReportCreatedPayload Payload);
