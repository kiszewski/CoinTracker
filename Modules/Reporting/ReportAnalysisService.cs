using System.Text.Json;
using CoinTracker.Infrastructure.Configuration;
using CoinTracker.Infrastructure.Persistence;
using CoinTracker.Shared.Messaging;
using CoinTracker.Shared.Observability;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CoinTracker.Modules.Reporting;

public sealed class ReportAnalysisService(
    MongoStore store,
    IEnumerable<IReportRule> rules,
    IOptions<AnalysisOptions> options,
    ILogger<ReportAnalysisService> logger)
{
    private const string ConsumerName = "report-analysis";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(EventEnvelope<MarketDataPopulatedPayload> message, CancellationToken cancellationToken)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = message.CorrelationId,
            ["EventId"] = message.EventId,
            ["IngestionRunId"] = message.Payload.IngestionRunId
        });
        var processed = store.Database.GetCollection<ProcessedMessageDocument>(MongoStore.ProcessedCollection);
        var processedId = ProcessedId(ConsumerName, message.EventId);
        if (await processed.Find(x => x.Id == processedId).AnyAsync(cancellationToken))
            return;

        var snapshots = store.Database.GetCollection<CoinSnapshotDocument>(MongoStore.SnapshotsCollection);
        var coinCodes = await snapshots.Distinct(x => x.CoinCode, Builders<CoinSnapshotDocument>.Filter.Empty)
            .ToListAsync(cancellationToken);
        var reportsToCreate = new List<(CoinReportDocument Report, List<Guid> Audience)>();
        var users = store.Database.GetCollection<UserDocument>(MongoStore.UsersCollection);

        foreach (var coinCode in coinCodes)
        {
            var latest = await snapshots.Find(x => x.CoinCode == coinCode)
                .SortByDescending(x => x.ObservedAtUtc)
                .ThenByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (latest is null || latest.UsdPrice <= 0)
                continue;

            var matchingUsers = await users.Find(x => x.FavoriteCoins.Any(f => f.CoinCode == coinCode)).ToListAsync(cancellationToken);
            if (matchingUsers.Count == 0)
                continue;
            var thresholds = matchingUsers.SelectMany(u => u.FavoriteCoins.Where(f => f.CoinCode == coinCode).Select(f => (u.Id, f.ThresholdPercent))).ToList();
            var minimumThreshold = thresholds.Min(x => x.ThresholdPercent);

            foreach (var rule in rules)
            {
                var defaultWindowStart = latest.ObservedAtUtc.AddHours(-options.Value.LookbackHours);
                var previousReport = await store.Database.GetCollection<CoinReportDocument>(MongoStore.ReportsCollection)
                    .Find(x => x.CoinCode == coinCode && x.ReportType == rule.ReportType && x.CreatedAtUtc > defaultWindowStart)
                    .SortByDescending(x => x.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                var effectiveStart = previousReport?.CreatedAtUtc ?? defaultWindowStart;

                var windowSnapshots = await snapshots.Find(x => x.CoinCode == coinCode &&
                                                                  x.ObservedAtUtc > defaultWindowStart &&
                                                                  x.ObservedAtUtc <= latest.ObservedAtUtc)
                    .SortBy(x => x.ObservedAtUtc)
                    .ToListAsync(cancellationToken);
                var report = rule.Analyze(latest, windowSnapshots, effectiveStart, message.CorrelationId, minimumThreshold);
                if (report is not null)
                    reportsToCreate.Add((report, thresholds.Where(x => Math.Abs(report.PercentChanged) >= x.ThresholdPercent).Select(x => x.Id).Distinct().ToList()));
            }
        }

        var createdCount = 0;
        var session = await store.Client.StartSessionAsync(cancellationToken: cancellationToken);
        using (session)
        {
            session.StartTransaction();
            try
            {
                if (!await processed.Find(session, x => x.Id == processedId).AnyAsync(cancellationToken))
                {
                    var reportCollection = store.Database.GetCollection<CoinReportDocument>(MongoStore.ReportsCollection);
                    var outbox = store.Database.GetCollection<OutboxMessageDocument>(MongoStore.OutboxCollection);
                    foreach (var candidate in reportsToCreate)
                    {
                        var report = candidate.Report;
                        if (await reportCollection.Find(session, x => x.IdempotencyKey == report.IdempotencyKey).AnyAsync(cancellationToken))
                            continue;

                        await reportCollection.InsertOneAsync(session, report, cancellationToken: cancellationToken);
                        var audienceCollection = store.Database.GetCollection<ReportAudienceDocument>(MongoStore.ReportAudiencesCollection);
                        if (candidate.Audience.Count > 0)
                            await audienceCollection.InsertManyAsync(session, candidate.Audience.Select(userId => new ReportAudienceDocument
                            {
                                Id = $"{report.Id:N}:{userId:N}", ReportId = report.Id, UserId = userId
                            }), cancellationToken: cancellationToken);
                        createdCount++;
                        var eventEnvelope = new EventEnvelope<ReportCreatedPayload>(
                            Guid.NewGuid(), EventTypes.ReportCreated, 1, DateTime.UtcNow,
                            message.CorrelationId, message.EventId, "cointracker.reporting",
                            new ReportCreatedPayload(report.Id, report.ReportType, report.RuleVersion, report.CoinCode, report.PeriodStartUtc, report.PeriodEndUtc));
                        await outbox.InsertOneAsync(session, new OutboxMessageDocument
                        {
                            Id = eventEnvelope.EventId,
                            EventType = eventEnvelope.EventType,
                            PayloadJson = JsonSerializer.Serialize(eventEnvelope, JsonOptions),
                            CorrelationId = eventEnvelope.CorrelationId,
                            CausationId = eventEnvelope.CausationId,
                            CreatedAtUtc = eventEnvelope.OccurredAtUtc
                        }, cancellationToken: cancellationToken);
                    }

                    await processed.InsertOneAsync(session, new ProcessedMessageDocument
                    {
                        Id = processedId,
                        Consumer = ConsumerName,
                        EventId = message.EventId,
                        ProcessedAtUtc = DateTime.UtcNow
                    }, cancellationToken: cancellationToken);
                }

                await session.CommitTransactionAsync(cancellationToken);
            }
            catch
            {
                await session.AbortTransactionAsync(cancellationToken);
                throw;
            }
        }

        if (createdCount > 0)
            CoinTrackerMetrics.ReportCreated("daily-price-movement", createdCount);

        logger.LogInformation("Analyzed latest stored market data for ingestion event {IngestionRunId}; created {ReportCount} report(s)",
            message.Payload.IngestionRunId, createdCount);
    }

    private static string ProcessedId(string consumer, Guid eventId) => $"{consumer}:{eventId:N}";
}
