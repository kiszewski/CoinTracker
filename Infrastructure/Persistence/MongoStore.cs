using CoinTracker.Infrastructure.Configuration;
using MongoDB.Driver;
using Microsoft.Extensions.Options;

namespace CoinTracker.Infrastructure.Persistence;

public sealed class MongoStore
{
    public const string SnapshotsCollection = "coin_snapshots";
    public const string RunsCollection = "ingestion_runs";
    public const string OutboxCollection = "outbox_messages";
    public const string ProcessedCollection = "processed_messages";
    public const string ReportsCollection = "reports";
    public const string DeliveriesCollection = "deliveries";
    public const string LeasesCollection = "worker_leases";

    public MongoStore(IMongoClient client, IOptions<MongoOptions> options)
    {
        Client = client;
        Database = client.GetDatabase(options.Value.DatabaseName);
    }

    public IMongoClient Client { get; }
    public IMongoDatabase Database { get; }

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var snapshots = Database.GetCollection<CoinSnapshotDocument>(SnapshotsCollection);
        await snapshots.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<CoinSnapshotDocument>(
                Builders<CoinSnapshotDocument>.IndexKeys.Ascending(x => x.CoinCode).Ascending(x => x.ObservedAtUtc)),
            new CreateIndexModel<CoinSnapshotDocument>(
                Builders<CoinSnapshotDocument>.IndexKeys.Ascending(x => x.CoinCode).Ascending(x => x.ObservedAtUtc).Ascending(x => x.Provider),
                new CreateIndexOptions { Unique = true, Name = "uq_snapshot_coin_time_provider" })
        }, cancellationToken: cancellationToken);

        var outbox = Database.GetCollection<OutboxMessageDocument>(OutboxCollection);
        await outbox.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<OutboxMessageDocument>(Builders<OutboxMessageDocument>.IndexKeys.Ascending(x => x.PublishedAtUtc).Ascending(x => x.CreatedAtUtc)),
            new CreateIndexModel<OutboxMessageDocument>(Builders<OutboxMessageDocument>.IndexKeys.Ascending(x => x.PublishedAtUtc),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(5), Name = "ttl_published_outbox_five_days" })
        }, cancellationToken: cancellationToken);

        var processed = Database.GetCollection<ProcessedMessageDocument>(ProcessedCollection);
        await processed.Indexes.CreateOneAsync(
            new CreateIndexModel<ProcessedMessageDocument>(Builders<ProcessedMessageDocument>.IndexKeys.Ascending(x => x.Consumer).Ascending(x => x.EventId), new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken);

        var reports = Database.GetCollection<CoinReportDocument>(ReportsCollection);
        await reports.Indexes.CreateOneAsync(
            new CreateIndexModel<CoinReportDocument>(Builders<CoinReportDocument>.IndexKeys.Ascending(x => x.IdempotencyKey), new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken);

        var deliveries = Database.GetCollection<DeliveryDocument>(DeliveriesCollection);
        await deliveries.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<DeliveryDocument>(Builders<DeliveryDocument>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.NextAttemptAtUtc)),
            new CreateIndexModel<DeliveryDocument>(Builders<DeliveryDocument>.IndexKeys.Ascending(x => x.Status).Ascending(x => x.LeaseExpiresAtUtc))
        }, cancellationToken: cancellationToken);

        await Database.GetCollection<OutboxPublishLeaseDocument>(LeasesCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<OutboxPublishLeaseDocument>(Builders<OutboxPublishLeaseDocument>.IndexKeys.Ascending(x => x.ExpiresAtUtc)),
            cancellationToken: cancellationToken);
    }
}
