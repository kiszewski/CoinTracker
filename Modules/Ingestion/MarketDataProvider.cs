using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoinTracker.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace CoinTracker.Modules.Ingestion;

public interface IMarketDataProvider
{
    Task<IReadOnlyList<GeckoCoinDto>> GetMarketsAsync(CancellationToken cancellationToken);
}

public sealed class CoinGeckoMarketDataProvider(HttpClient httpClient, IOptions<MarketDataOptions> options, ILogger<CoinGeckoMarketDataProvider> logger) : IMarketDataProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<GeckoCoinDto>> GetMarketsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.Value.MarketsUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(options.Value.ApiKey))
            request.Headers.Add("x-cg-demo-api-key", options.Value.ApiKey);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var coins = await JsonSerializer.DeserializeAsync<List<GeckoCoinDto>>(stream, JsonOptions, cancellationToken);
        if (coins is null)
            throw new InvalidDataException("The market-data provider returned an empty response body.");

        var invalidCoins = coins.Where(coin => string.IsNullOrWhiteSpace(coin.Id) || string.IsNullOrWhiteSpace(coin.Name) || coin.CurrentPrice <= 0).ToList();
        if (invalidCoins.Count > 0)
            throw new InvalidDataException($"The market-data provider returned {invalidCoins.Count} record(s) with missing identity or a non-positive price.");

        logger.LogInformation("Fetched {AssetCount} market records from CoinGecko", coins.Count);
        return coins;
    }
}

public sealed class GeckoCoinDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("current_price")]
    public decimal CurrentPrice { get; set; }

    [JsonPropertyName("market_cap")]
    public long MarketCap { get; set; }

    [JsonPropertyName("total_volume")]
    public decimal TotalVolume { get; set; }

    [JsonPropertyName("last_updated")]
    public DateTime LastUpdated { get; set; }
}
