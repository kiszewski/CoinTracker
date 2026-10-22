using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CoinTracker.Infrastructure.Persistence;

public sealed class MongoInitializationService(MongoStore store, ILogger<MongoInitializationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing MongoDB indexes");
        await store.EnsureIndexesAsync(cancellationToken);
        await MigrateLegacySnapshotsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task MigrateLegacySnapshotsAsync(CancellationToken cancellationToken)
    {
        const string migrationId = "embedded-coin-snapshots-v1";
        var migrations = store.Database.GetCollection<BsonDocument>("schema_migrations");
        if (await migrations.Find(x => x["_id"] == migrationId).AnyAsync(cancellationToken))
            return;

        var legacyCoins = await store.Database.GetCollection<BsonDocument>("coins")
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(cancellationToken);
        var snapshots = store.Database.GetCollection<CoinSnapshotDocument>(MongoStore.SnapshotsCollection);
        var migratedCount = 0;

        foreach (var coin in legacyCoins)
        {
            var code = coin.GetValue("Code", "").ToString() ?? "";
            var name = coin.GetValue("Name", code).ToString() ?? code;
            if (string.IsNullOrWhiteSpace(code) || !coin.TryGetValue("Snapshots", out var snapshotArray) || !snapshotArray.IsBsonArray)
                continue;

            foreach (var legacySnapshot in snapshotArray.AsBsonArray)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!legacySnapshot.IsBsonDocument)
                    continue;

                var snapshot = legacySnapshot.AsBsonDocument;
                if (!snapshot.TryGetValue("date", out var dateValue) || !dateValue.IsBsonDateTime ||
                    !snapshot.TryGetValue("DolarPrice", out var priceValue))
                    continue;

                var observedAt = snapshot["date"].ToUniversalTime();
                decimal price;
                try
                {
                    price = priceValue.ToDecimal();
                }
                catch (FormatException)
                {
                    continue;
                }
                if (price <= 0)
                    continue;

                var id = $"legacy:{code}:{observedAt.Ticks}";
                var migrated = new CoinSnapshotDocument
                {
                    Id = id,
                    CoinCode = code,
                    CoinName = name,
                    Provider = "legacy",
                    ObservedAtUtc = observedAt,
                    UsdPrice = price,
                    UsdMarketCap = snapshot.TryGetValue("DolarMarketCap", out var cap) && cap.IsNumeric ? cap.ToInt64() : 0,
                    UsdVolume = snapshot.TryGetValue("Volume", out var volume) && volume.IsNumeric ? volume.ToDecimal() : 0
                };

                await snapshots.ReplaceOneAsync(x => x.Id == id, migrated, new ReplaceOptions { IsUpsert = true }, cancellationToken);
                migratedCount++;
            }
        }

        try
        {
            await migrations.InsertOneAsync(new BsonDocument
            {
                ["_id"] = migrationId,
                ["completedAtUtc"] = DateTime.UtcNow,
                ["snapshotCount"] = migratedCount
            }, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            logger.LogInformation("Legacy snapshot migration was already completed by another application instance");
            return;
        }
        logger.LogInformation("Migrated {SnapshotCount} legacy coin snapshots", migratedCount);
    }
}
