using Microsoft.AspNetCore.Mvc;
using CoinTracker.Infrastructure.Configuration;
using CoinTracker.Infrastructure.Persistence;
using CoinTracker.Modules.Delivery;
using CoinTracker.Modules.Ingestion;
using CoinTracker.Modules.Reporting;
using CoinTracker.Modules.Users;
using CoinTracker.Shared.Messaging;
using CoinTracker.Shared.Observability;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Use one explicit GUID representation for all persisted Guid fields. Without this,
// MongoDB.Driver 3.x leaves GuidRepresentation unspecified and rejects serialization.
BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

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
    .Validate(x => x.PopulationIntervalMinutes > 0, "MarketData:PopulationIntervalMinutes must be greater than zero.")
    .ValidateOnStart();
builder.Services.AddOptions<AnalysisOptions>()
    .Bind(builder.Configuration.GetSection("Analysis"))
    .Validate(x => x.LookbackHours > 0, "Daily analysis lookback must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<UserSecretsOptions>()
    .Bind(builder.Configuration.GetSection("UserSecrets"))
    .Validate(x => { try { return Convert.FromBase64String(x.EncryptionKey).Length == 32; } catch { return false; } }, "UserSecrets:EncryptionKey must be a base64-encoded 32-byte key.")
    .ValidateOnStart();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));

builder.Services.AddSingleton<IMongoClient>(services =>
{
    var mongoOptions = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MongoOptions>>().Value;
    return new MongoClient(MongoClientSettings.FromConnectionString(mongoOptions.ConnectionString));
});
builder.Services.AddSingleton<MongoStore>();
builder.Services.AddSingleton<UserProfileService>();
builder.Services.AddSingleton<RabbitMqConnectionFactory>();

builder.Services.AddHttpClient<IMarketDataProvider, CoinGeckoMarketDataProvider>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<PopulateMarketData>();
builder.Services.AddScoped<ReportAnalysisService>();
builder.Services.AddSingleton<IReportRule, DailyPriceMovementRule>();
builder.Services.AddScoped<DeliveryInboxService>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<IReportEmailTemplateRenderer, ReportEmailTemplateRenderer>();
builder.Services.AddSingleton<IDeliveryChannel, EmailDeliveryChannel>();
builder.Services.AddSingleton<IDeliveryChannel, DiscordDeliveryChannel>();
builder.Services.AddHttpClient("discord", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

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
app.MapPost("/api/users", async (UserCreateRequest request, UserProfileService users, CancellationToken token) =>
{
    try { var profile = await users.CreateAsync(request, token); return Results.Created($"/api/users/{profile.UserId}", profile); }
    catch (UserValidationException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (UserConflictException e) { return Results.Conflict(new { error = e.Message }); }
});
app.MapPost("/api/users/lookup", async (EmailLookupRequest request, UserProfileService users, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(request.Email)) return Results.BadRequest(new { error = "Email is required." });
    var id = await users.LookupAsync(request.Email, token);
    return id is null ? Results.NotFound() : Results.Ok(new { userId = id });
});
app.MapGet("/api/users/{userId:guid}", async (Guid userId, UserProfileService users, CancellationToken token) =>
    await users.GetAsync(userId, token) is { } profile ? Results.Ok(profile) : Results.NotFound());
app.MapPut("/api/users/{userId:guid}", async (Guid userId, UserUpdateRequest request, UserProfileService users, CancellationToken token) =>
{
    try { return await users.UpdateAsync(userId, request, token) is { } profile ? Results.Ok(profile) : Results.NotFound(); }
    catch (UserValidationException e) { return Results.BadRequest(new { error = e.Message }); }
    catch (UserConflictException e) { return Results.Conflict(new { error = e.Message }); }
});
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
