using System.Text.Json;
using CoinTracker.Infrastructure.Configuration;
using CoinTracker.Infrastructure.Persistence;
using CoinTracker.Shared.Messaging;
using CoinTracker.Shared.Observability;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CoinTracker.Modules.Ingestion;

public sealed class PopulateMarketData(
    IMarketDataProvider provider,
    MongoStore store,
    ILogger<PopulateMarketData> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["IngestionRunId"] = runId
        });

        try
        {
            var remoteCoins = await provider.GetMarketsAsync(cancellationToken);
            if (remoteCoins.Count == 0)
                throw new InvalidDataException("The market-data provider returned no market records.");

            var observedAt = DateTime.UtcNow;
            var snapshots = remoteCoins.Take(50).Select(remote =>
            {
                return new CoinSnapshotDocument
                {
                    Id = $"coingecko:{remote.Id}:{observedAt.Ticks}",
                    CoinCode = remote.Id,
                    CoinName = remote.Name,
                    Provider = "coingecko",
                    IngestionRunId = runId,
                    ObservedAtUtc = observedAt,
                    UsdPrice = remote.CurrentPrice,
                    UsdMarketCap = remote.MarketCap,
                    UsdVolume = remote.TotalVolume
                };
            }).ToList();

            var session = await store.Client.StartSessionAsync(cancellationToken: cancellationToken);
            using (session)
            {
                session.StartTransaction();
                try
                {
                    await store.Database.GetCollection<IngestionRunDocument>(MongoStore.RunsCollection)
                        .InsertOneAsync(session, new IngestionRunDocument
                        {
                            Id = runId,
                            Provider = "coingecko",
                            Status = "succeeded",
                            StartedAtUtc = startedAt,
                            FinishedAtUtc = DateTime.UtcNow,
                            CorrelationId = correlationId,
                            AssetCount = remoteCoins.Count,
                            SnapshotCount = snapshots.Count
                        }, cancellationToken: cancellationToken);

                    var snapshotCollection = store.Database.GetCollection<CoinSnapshotDocument>(MongoStore.SnapshotsCollection);
                    foreach (var snapshot in snapshots)
                    {
                        await snapshotCollection.ReplaceOneAsync(
                            session,
                            x => x.Id == snapshot.Id,
                            snapshot,
                            new ReplaceOptions { IsUpsert = true },
                            cancellationToken);
                    }

                    var payload = new MarketDataPopulatedPayload(runId, "coingecko", observedAt, remoteCoins.Count, snapshots.Count);
                    var envelope = new EventEnvelope<MarketDataPopulatedPayload>(
                        Guid.NewGuid(), EventTypes.MarketDataPopulated, 1, DateTime.UtcNow, correlationId, null, "cointracker.ingestion", payload);
                    await store.Database.GetCollection<OutboxMessageDocument>(MongoStore.OutboxCollection)
                        .InsertOneAsync(session, new OutboxMessageDocument
                        {
                            Id = envelope.EventId,
                            EventType = envelope.EventType,
                            PayloadJson = JsonSerializer.Serialize(envelope, JsonOptions),
                            CorrelationId = correlationId,
                            CreatedAtUtc = DateTime.UtcNow
                        }, cancellationToken: cancellationToken);

                    await session.CommitTransactionAsync(cancellationToken);
                }
                catch
                {
                    await session.AbortTransactionAsync(cancellationToken);
                    throw;
                }
            }

            logger.LogInformation("Ingestion run {IngestionRunId} stored {SnapshotCount} snapshots", runId, snapshots.Count);
            CoinTrackerMetrics.IngestionSucceeded(snapshots.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Ingestion run {IngestionRunId} failed", runId);
            CoinTrackerMetrics.IngestionFailed();
            try
            {
                await store.Database.GetCollection<IngestionRunDocument>(MongoStore.RunsCollection).InsertOneAsync(
                    new IngestionRunDocument
                    {
                        Id = runId,
                        Provider = "coingecko",
                        Status = "failed",
                        StartedAtUtc = startedAt,
                        FinishedAtUtc = DateTime.UtcNow,
                        CorrelationId = correlationId,
                        Error = exception.GetType().Name
                    }, cancellationToken: cancellationToken);
            }
            catch (Exception persistException)
            {
                logger.LogError(persistException, "Could not persist failed ingestion run {IngestionRunId}", runId);
            }
        }
    }

}

public sealed class MarketDataPopulationWorker(
    IServiceScopeFactory scopeFactory,
    MongoStore store,
    IOptions<MarketDataOptions> options,
    ILogger<MarketDataPopulationWorker> logger) : BackgroundService
{
    private readonly string _owner = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.PopulationIntervalHours));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                if (await TryAcquireRunLeaseAsync(stoppingToken))
                {
                    try
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<PopulateMarketData>().RunAsync(stoppingToken);
                    }
                    finally
                    {
                        await ReleaseRunLeaseAsync(CancellationToken.None);
                    }
                }
                else
                {
                    logger.LogInformation("Skipped scheduled population because another instance holds the run lease");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduled market-data population failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<bool> TryAcquireRunLeaseAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var leases = store.Database.GetCollection<OutboxPublishLeaseDocument>(MongoStore.LeasesCollection);
        var filter = Builders<OutboxPublishLeaseDocument>.Filter.And(
            Builders<OutboxPublishLeaseDocument>.Filter.Eq(x => x.Id, "market-data-population"),
            Builders<OutboxPublishLeaseDocument>.Filter.Or(
                Builders<OutboxPublishLeaseDocument>.Filter.Lt(x => x.ExpiresAtUtc, now),
                Builders<OutboxPublishLeaseDocument>.Filter.Eq(x => x.Owner, _owner)));
        var update = Builders<OutboxPublishLeaseDocument>.Update
            .SetOnInsert(x => x.Id, "market-data-population")
            .Set(x => x.Owner, _owner)
            .Set(x => x.ExpiresAtUtc, now.AddMinutes(5));
        try
        {
            var lease = await leases.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<OutboxPublishLeaseDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, cancellationToken);
            return lease?.Owner == _owner;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    private Task ReleaseRunLeaseAsync(CancellationToken cancellationToken)
    {
        return store.Database.GetCollection<OutboxPublishLeaseDocument>(MongoStore.LeasesCollection)
            .UpdateOneAsync(x => x.Id == "market-data-population" && x.Owner == _owner,
                Builders<OutboxPublishLeaseDocument>.Update.Set(x => x.ExpiresAtUtc, DateTime.UtcNow),
                cancellationToken: cancellationToken);
    }
}
