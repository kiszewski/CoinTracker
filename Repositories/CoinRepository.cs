using System.Net.Http.Headers;
using System.Text.Json;

class CoinRepository
{
    private HttpClient _client;

    public CoinRepository(HttpClient client)
    {
        _client = client;
    }

    public async Task<IEnumerable<GeckoCoinDto>> GetRemoteCoinRecords()
    {
        try
        {
            var apiKey = Environment.GetEnvironmentVariable("API_KEY");

            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri("https://api.coingecko.com/api/v3/coins/list"),
                Headers =
    {
        { "accept", "application/json" },
        { "x-cg-pro-api-key", apiKey },
    },
            };
            using (var response = await _client.SendAsync(request))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();

                var coins = JsonSerializer.Deserialize<IEnumerable<GeckoCoinDto>>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return coins ?? Enumerable.Empty<GeckoCoinDto>();
            }
        }
        catch (Exception)
        {
            return Enumerable.Empty<GeckoCoinDto>();
        }
    }
    public async Task AddCoinRecords(IEnumerable<Coin> coins)
    {

    }
    public async Task<IEnumerable<Coin>> GetLocalCoinRecords()
    {
        return Enumerable.Empty<Coin>();
    }
}