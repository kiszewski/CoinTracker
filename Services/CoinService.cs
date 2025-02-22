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
        var reports = Enumerable.Empty<CoinReport>();
        var coins = await _repository.GetLocalCoinRecords();

        foreach (var coin in coins)
        {
            var lastSnapshots = coin.Snapshots.OrderBy(e => e.date).TakeLast(2);

            var oldSnaphot = lastSnapshots.First();
            var newSnaphot = lastSnapshots.Last();

            var difference = oldSnaphot.DolarPrice - newSnaphot.DolarPrice;

            var percent = 0.01m;

            var dolarPercent = oldSnaphot.DolarPrice * percent;

            if (Math.Abs(difference) >= dolarPercent)
            {
                var report = new CoinReport
                {
                    Coin = coin,
                    NewDolarPrice = newSnaphot.DolarPrice,
                    OldDolarPrice = oldSnaphot.DolarPrice,
                    NewDolarPriceDate = newSnaphot.date,
                    OldDolarPriceDate = oldSnaphot.date,
                };

                reports.Append(report);
            }
        }

        return reports;
    }
}