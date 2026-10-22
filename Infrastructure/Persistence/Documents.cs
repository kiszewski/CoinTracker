using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace CoinTracker.Infrastructure.Persistence;

public sealed class CoinSnapshotDocument
{
    [BsonId]
    public string Id { get; set; } = "";
    public string CoinCode { get; set; } = "";
    public string CoinName { get; set; } = "";
    public string Provider { get; set; } = "coingecko";
    public Guid IngestionRunId { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public decimal UsdPrice { get; set; }
    public long UsdMarketCap { get; set; }
    public decimal UsdVolume { get; set; }
}

public sealed class IngestionRunDocument
{
    [BsonId]
    public Guid Id { get; set; }
    public string Provider { get; set; } = "coingecko";
    public string Status { get; set; } = "succeeded";
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public Guid CorrelationId { get; set; }
    public int AssetCount { get; set; }
    public int SnapshotCount { get; set; }
    public string? Error { get; set; }
}

public sealed class OutboxMessageDocument
{
    [BsonId]
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public int PublishAttempts { get; set; }
}

public sealed class ProcessedMessageDocument
{
    [BsonId]
    public string Id { get; set; } = "";
    public string Consumer { get; set; } = "";
    public Guid EventId { get; set; }
    public DateTime ProcessedAtUtc { get; set; }
}

public sealed record CoinReportDocument
{
    [BsonId]
    public Guid Id { get; init; }
    public string IdempotencyKey { get; init; } = "";
    public string ReportType { get; init; } = "daily-price-movement";
    public int RuleVersion { get; init; }
    public string CoinCode { get; init; } = "";
    public string CoinName { get; init; } = "";
    public string LatestSnapshotId { get; init; } = "";
    public decimal OldUsdPrice { get; init; }
    public decimal NewUsdPrice { get; init; }
    public decimal PercentChanged { get; init; }
    public DateTime OldPriceAtUtc { get; init; }
    public DateTime NewPriceAtUtc { get; init; }
    public DateTime PeriodStartUtc { get; init; }
    public DateTime PeriodEndUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public Guid CorrelationId { get; init; }
}

public sealed class DeliveryDocument
{
    [BsonId]
    public string Id { get; set; } = "";
    public Guid ReportId { get; set; }
    public Guid CorrelationId { get; set; }
    public string RecipientEmail { get; set; } = "";
    public string RecipientName { get; set; } = "";
    public string Channel { get; set; } = "email";
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? LeaseOwner { get; set; }
    public string? LastError { get; set; }
}

public sealed class OutboxPublishLeaseDocument
{
    [BsonId]
    public string Id { get; set; } = "outbox-publisher";
    public string Owner { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}
