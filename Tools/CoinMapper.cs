public static class CoinMapper
{
    public static Coin GeckoCoinToCoin(this GeckoCoinDto coinDto)
    {
        return new Coin
        {
            Code = coinDto.Id,
            Name = coinDto.Name,
            DolarMarketCap = coinDto.MarketCap,
            DolarPrice = coinDto.CurrentPrice,
            Volume = coinDto.TotalVolume,
        };
    }
}