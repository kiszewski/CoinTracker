namespace CoinTracker.Infrastructure.Configuration;

public sealed class MongoOptions
{
    public string ConnectionString { get; set; } = "";
    public string DatabaseName { get; set; } = "CoinsDB";
}

public sealed class RabbitMqOptions
{
    public string ConnectionString { get; set; } = "";
    public string ExchangeName { get; set; } = "cointracker.events";
    public string DeadLetterExchangeName { get; set; } = "cointracker.dead-letter";
    public int RetryCount { get; set; } = 5;
    public int RetryIntervalSeconds { get; set; } = 30;
}

public sealed class MarketDataOptions
{
    public string ApiKey { get; set; } = "";
    public string MarketsUrl { get; set; } = "https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&per_page=50&page=1";
    public int PopulationIntervalMinutes { get; set; } = 15;
}

public sealed class AnalysisOptions
{
    public decimal MovementThresholdPercent { get; set; } = 5m;
    public int LookbackHours { get; set; } = 24;
}

public sealed class EmailOptions
{
    public string ApiKey { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SenderPassword { get; set; } = "";
    public string SenderName { get; set; } = "CoinTracker";
}
