using DotNetEnv;
using Microsoft.AspNetCore.Mvc;
using MimeKit;
using MimeKit.Text;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi

Env.Load();

builder.Services.AddOpenApi();

builder.Services.AddTransient<IMongoClient>(e =>
{
    var connectionUri = Env.GetString("MONGO_DB");
    var settings = MongoClientSettings.FromConnectionString(connectionUri);

    return new MongoClient(settings);
});

builder.Services.AddHttpClient<CoinRepository>();
builder.Services.AddScoped<CoinService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<CoinUpdatesNotifierService>();

builder.Services.AddHostedService(e =>
{
    var logger = e.GetService<ILogger<CustomBackgroundService>>();
    var logger2 = e.GetService<ILogger<CoinUpdatesNotifierService>>();

    var coinRepository = e.GetService<CoinRepository>();
    var coinService = new CoinService(coinRepository!);
    var emailService = new EmailService();
    var notifierService = new CoinUpdatesNotifierService(coinService, emailService, logger2!);

    return new CustomBackgroundService(logger!, async () =>
    {
        await coinService!.PopulateCoins();
        await notifierService!.CheckUpdates();
        await notifierService!.SendWeeklyReport();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/process", async ([FromServices] CoinUpdatesNotifierService service) =>
{
    await service.CheckUpdates();

    return Results.Ok();
});

app.MapGet("/testEmail", async ([FromServices] EmailService service) =>
{
    string html = $@"
        <html>
        <head>
            <style>
                body {{
                    font-family: 'Segoe UI', sans-serif;
                    background-color: #f9f9f9;
                    padding: 20px;
                }}
                .card {{
                    background-color: white;
                    border-radius: 12px;
                    padding: 24px;
                    max-width: 500px;
                    margin: auto;
                    box-shadow: 0 2px 10px rgba(0,0,0,0.1);
                }}
                .title {{
                    font-size: 20px;
                    font-weight: bold;
                    color: #333;
                }}
                .priceHigh{{
                    font-size: 32px;
                    font-weight: bold;
                    color: #28a745;
                }}
                .priceLow {{
                    font-size: 32px;
                    font-weight: bold;
                    color: #dc3545;
                }}
            </style>
        </head>
        <body>
            <div class='card'>
                <div class='title'>Teste report weekly</div>
                <div class='priceHigh'>▲ $100 USD</div>
                <div class='priceLow'>▼ $50 USD</div>
            </div>
        </body>
        </html>
        ";

    var input = new SendEmailParams
    {
        RecipientEmail = "kiszewski1999@gmail.com",
        RecipientName = "Léo",
        Body = new TextPart(TextFormat.Html) { Text = html },
        Subject = "Report"
    };

    var response = await service.SendEmail(input);

    return Results.Ok($"Response: {response}");
});

app.Run();