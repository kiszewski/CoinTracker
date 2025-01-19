using System;
using System.Text.Json.Serialization;

public class GeckoCoinDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("image")]
    public string Image { get; set; }

    [JsonPropertyName("current_price")]
    public decimal CurrentPrice { get; set; }

    [JsonPropertyName("market_cap")]
    public long MarketCap { get; set; }

    [JsonPropertyName("market_cap_rank")]
    public int MarketCapRank { get; set; }

    [JsonPropertyName("fully_diluted_valuation")]
    public long? FullyDilutedValuation { get; set; }

    [JsonPropertyName("total_volume")]
    public long TotalVolume { get; set; }

    [JsonPropertyName("high_24h")]
    public decimal High24h { get; set; }

    [JsonPropertyName("low_24h")]
    public decimal Low24h { get; set; }

    [JsonPropertyName("price_change_24h")]
    public decimal PriceChange24h { get; set; }

    [JsonPropertyName("price_change_percentage_24h")]
    public double PriceChangePercentage24h { get; set; }

    [JsonPropertyName("market_cap_change_24h")]
    public long MarketCapChange24h { get; set; }

    [JsonPropertyName("market_cap_change_percentage_24h")]
    public double MarketCapChangePercentage24h { get; set; }

    [JsonPropertyName("circulating_supply")]
    public decimal CirculatingSupply { get; set; }

    [JsonPropertyName("total_supply")]
    public decimal? TotalSupply { get; set; }

    [JsonPropertyName("max_supply")]
    public decimal? MaxSupply { get; set; }

    [JsonPropertyName("ath")]
    public decimal AllTimeHigh { get; set; }

    [JsonPropertyName("ath_change_percentage")]
    public double AllTimeHighChangePercentage { get; set; }

    [JsonPropertyName("ath_date")]
    public DateTime AllTimeHighDate { get; set; }

    [JsonPropertyName("atl")]
    public decimal AllTimeLow { get; set; }

    [JsonPropertyName("atl_change_percentage")]
    public double AllTimeLowChangePercentage { get; set; }

    [JsonPropertyName("atl_date")]
    public DateTime AllTimeLowDate { get; set; }

    [JsonPropertyName("roi")]
    public object Roi { get; set; }

    [JsonPropertyName("last_updated")]
    public DateTime LastUpdated { get; set; }
}
