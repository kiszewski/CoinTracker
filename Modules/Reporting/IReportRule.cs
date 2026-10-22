using CoinTracker.Infrastructure.Persistence;
using CoinTracker.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace CoinTracker.Modules.Reporting;

public interface IReportRule
{
    string ReportType { get; }
    int Version { get; }
    CoinReportDocument? Analyze(
        CoinSnapshotDocument latest,
        IReadOnlyList<CoinSnapshotDocument> lookbackSnapshots,
        DateTime effectiveWindowStartUtc,
        Guid correlationId);
}

public sealed class DailyPriceMovementRule(IOptions<AnalysisOptions> options) : IReportRule
{
    public string ReportType => "daily-price-movement";
    public int Version => 1;

    public CoinReportDocument? Analyze(
        CoinSnapshotDocument latest,
        IReadOnlyList<CoinSnapshotDocument> lookbackSnapshots,
        DateTime effectiveWindowStartUtc,
        Guid correlationId)
    {
        if (latest.UsdPrice <= 0)
            return null;

        var largestChange = lookbackSnapshots
            .Where(snapshot => snapshot.Id != latest.Id && snapshot.ObservedAtUtc > effectiveWindowStartUtc && snapshot.ObservedAtUtc <= latest.ObservedAtUtc && snapshot.UsdPrice > 0)
            .Select(snapshot => new
            {
                Snapshot = snapshot,
                Percent = (latest.UsdPrice - snapshot.UsdPrice) / snapshot.UsdPrice * 100m
            })
            .OrderByDescending(change => Math.Abs(change.Percent))
            .FirstOrDefault();

        if (largestChange is null || Math.Abs(largestChange.Percent) < options.Value.MovementThresholdPercent)
            return null;

        return new CoinReportDocument
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = $"{ReportType}:v{Version}:{latest.CoinCode}:{latest.Id}",
            ReportType = ReportType,
            RuleVersion = Version,
            CoinCode = latest.CoinCode,
            CoinName = latest.CoinName,
            LatestSnapshotId = latest.Id,
            OldUsdPrice = largestChange.Snapshot.UsdPrice,
            NewUsdPrice = latest.UsdPrice,
            PercentChanged = largestChange.Percent,
            OldPriceAtUtc = largestChange.Snapshot.ObservedAtUtc,
            NewPriceAtUtc = latest.ObservedAtUtc,
            PeriodStartUtc = effectiveWindowStartUtc,
            PeriodEndUtc = latest.ObservedAtUtc,
            CreatedAtUtc = DateTime.UtcNow,
            CorrelationId = correlationId
        };
    }
}
