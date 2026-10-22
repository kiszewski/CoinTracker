using Microsoft.AspNetCore.Mvc;
using CoinTracker.Infrastructure.Configuration;
using CoinTracker.Infrastructure.Persistence;
using CoinTracker.Modules.Delivery;
using CoinTracker.Modules.Ingestion;
using CoinTracker.Modules.Reporting;
using CoinTracker.Shared.Messaging;
using CoinTracker.Shared.Observability;
using MongoDB.Bson;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);
builder.Services.AddOpenApi();

builder.Services.AddOptions<MongoOptions>()
    .Bind(builder.Configuration.GetSection("Mongo"))
    .Validate(x => Uri.TryCreate(x.ConnectionString, UriKind.Absolute, out _), "Mongo:ConnectionString must be a valid MongoDB URI.")
    .ValidateOnStart();
builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection("RabbitMq"))
    .Validate(x => Uri.TryCreate(x.ConnectionString, UriKind.Absolute, out var uri) && uri.Scheme is "amqp" or "amqps", "RabbitMq:ConnectionString must be an amqp or amqps URI.")
    .Validate(x => x.RetryCount == 5 && x.RetryIntervalSeconds == 30, "The current event retry policy is five retries at 30-second intervals.")
    .ValidateOnStart();
builder.Services.AddOptions<MarketDataOptions>()
    .Bind(builder.Configuration.GetSection("MarketData"))
    .Validate(x => Uri.TryCreate(x.MarketsUrl, UriKind.Absolute, out _), "MarketData:MarketsUrl must be an absolute URI.")
    .Validate(x => x.PopulationIntervalHours > 0, "MarketData:PopulationIntervalHours must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddOptions<AnalysisOptions>()
    .Bind(builder.Configuration.GetSection("Analysis"))
    .Validate(x => x.MovementThresholdPercent > 0 && x.LookbackHours > 0, "Daily analysis threshold and lookback must be positive.")
    .ValidateOnStart();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));

builder.Services.AddSingleton<IMongoClient>(services =>
{
    var mongoOptions = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MongoOptions>>().Value;
    return new MongoClient(MongoClientSettings.FromConnectionString(mongoOptions.ConnectionString));
});
builder.Services.AddSingleton<MongoStore>();
builder.Services.AddSingleton<RabbitMqConnectionFactory>();

builder.Services.AddHttpClient<IMarketDataProvider, CoinGeckoMarketDataProvider>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<PopulateMarketData>();
builder.Services.AddScoped<ReportAnalysisService>();
builder.Services.AddSingleton<IReportRule, DailyPriceMovementRule>();
builder.Services.AddScoped<DeliveryInboxService>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<IReportEmailTemplateRenderer, ReportEmailTemplateRenderer>();
builder.Services.AddSingleton<IDeliveryChannel, EmailDeliveryChannel>();

builder.Services.AddHostedService<MongoInitializationService>();
builder.Services.AddHostedService<MarketDataPopulationWorker>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<EventConsumersWorker>();
builder.Services.AddHostedService<EmailDeliveryWorker>();
builder.Services.AddHostedService<RabbitMqQueueMetricsWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (
    MongoStore store,
    RabbitMqConnectionFactory rabbitConnectionFactory,
    Microsoft.Extensions.Options.IOptions<MarketDataOptions> marketDataOptions,
    Microsoft.Extensions.Options.IOptions<EmailOptions> emailOptions,
    CancellationToken cancellationToken) =>
{
    try
    {
        await store.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
        using var connection = rabbitConnectionFactory.CreateConnection();
        var providerConfigured = !string.IsNullOrWhiteSpace(marketDataOptions.Value.ApiKey);
        var emailConfigured = !string.IsNullOrWhiteSpace(emailOptions.Value.ApiKey) &&
                              !string.IsNullOrWhiteSpace(emailOptions.Value.SenderEmail) &&
                              !string.IsNullOrWhiteSpace(emailOptions.Value.SenderPassword);
        if (!providerConfigured || !emailConfigured)
            return Results.Json(new { status = "not-ready", providerConfigured, emailConfigured }, statusCode: StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new { status = "ready" });
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
