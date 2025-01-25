using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IMongoClient>(e =>
{
    var connectionUri = Env.GetString("MONGO_DB");
    var settings = MongoClientSettings.FromConnectionString(connectionUri);

    return new MongoClient(settings);
});
builder.Services.AddHttpClient<CoinRepository>();
builder.Services.AddSingleton<CoinService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapGet("/process", async ([FromServices] CoinService service) =>
{
    await service.PopulateCoins();

    return Results.Ok();
});

app.Run();