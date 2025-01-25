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
            await _repository.AddCoinRecords(coins);
        }
    }

    public async Task AnalyzeCoins()
    {
        await _repository.GetLocalCoinRecords();
    }
}