class CoinService
{
    private CoinRepository _repository;

    public CoinService(CoinRepository repository)
    {
        _repository = repository;
    }

    public async Task PopulateCoins()
    {
        var remoteRecords = await _repository.GetRemoteCoinRecords();

        if (remoteRecords.Any())
        {
            var coins = remoteRecords.Take(50).Select(x => x.GeckoCoinToCoin());
            await _repository.AddOrUpdateCoinRecords(coins);
        }
    }

    public async Task<IEnumerable<CoinReport>> AnalyzeCoinsDaily()
    {
        var reports = new List<CoinReport> { };
        var coins = await _repository.GetLocalCoinRecords();

        foreach (var coin in coins)
        {
            var lastSnapshots = coin.Snapshots.OrderBy(e => e.date).TakeLast(12);

            var last = lastSnapshots.Last();
            var lowest = lastSnapshots.MinBy(e => e.DolarPrice);
            var highest = lastSnapshots.MaxBy(e => e.DolarPrice);

            var differenceToLowest = last.DolarPrice - lowest!.DolarPrice;
            var differenceToHighest = last.DolarPrice - highest!.DolarPrice;

            var percent = 0.05m;

            var dolarPercent = last.DolarPrice * percent;

            if (Math.Abs(differenceToLowest) >= dolarPercent)
            {
                var report = new CoinReport
                {
                    Coin = coin,
                    NewDolarPrice = last.DolarPrice,
                    OldDolarPrice = lowest.DolarPrice,
                    NewDolarPriceDate = last.date,
                    OldDolarPriceDate = lowest.date,
                };

                reports.Add(report);
            }

            if (Math.Abs(differenceToHighest) >= dolarPercent)
            {
                var report = new CoinReport
                {
                    Coin = coin,
                    NewDolarPrice = last.DolarPrice,
                    OldDolarPrice = highest.DolarPrice,
                    NewDolarPriceDate = last.date,
                    OldDolarPriceDate = highest.date,
                };

                reports.Add(report);
            }
        }

        return reports;
    }

    public async Task<CoinWeeklyReport?> AnalyzeCoinWeekly(Coin coin)
    {
        var localCoin = await _repository.GetCoin(coin.Code);
        var lastWeekStartDate = DateTimeOffset.Now.Subtract(TimeSpan.FromDays(7));

        if (localCoin == null) return null;

        var lastSnapshots = localCoin.Snapshots.Where(s => s.date > lastWeekStartDate).ToArray();

        var highest = lastSnapshots.MaxBy(e => e.DolarPrice);
        var lowest = lastSnapshots.MinBy(e => e.DolarPrice);

        var report = new CoinWeeklyReport
        {
            Coin = localCoin,
            Highest = highest?.DolarPrice ?? 0,
            Lowest = lowest?.DolarPrice ?? 0,
        };

        return report;
    }
}