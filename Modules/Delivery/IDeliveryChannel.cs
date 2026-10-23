using CoinTracker.Infrastructure.Persistence;
using CoinTracker.Modules.Users;
using MongoDB.Driver;
using System.Net;
using System.Text.Encodings.Web;

namespace CoinTracker.Modules.Delivery;

public interface IDeliveryChannel
{
    string Name { get; }
    Task DeliverAsync(DeliveryDocument delivery, CoinReportDocument report, CancellationToken cancellationToken);
}

public interface IReportEmailTemplateRenderer
{
    string Render(CoinReportDocument report);
}

public sealed class ReportEmailTemplateRenderer : IReportEmailTemplateRenderer
{
    public string Render(CoinReportDocument report)
    {
        var safeName = HtmlEncoder.Default.Encode(report.CoinName);
        var safeCode = HtmlEncoder.Default.Encode(report.CoinCode.ToUpperInvariant());
        var color = report.PercentChanged >= 0 ? "#28a745" : "#dc3545";
        var arrow = report.PercentChanged >= 0 ? "▲" : "▼";
        return $"""
            <html><body style="font-family:Segoe UI,sans-serif;background:#f9f9f9;padding:20px">
              <div style="background:white;border-radius:12px;padding:24px;max-width:500px;margin:auto">
                <div style="font-size:20px;font-weight:bold;color:#333">{safeName} ({safeCode})</div>
                <div style="font-size:32px;font-weight:bold;color:{color}">{arrow} ${report.NewUsdPrice:F2} USD</div>
                <p><strong>Change:</strong> {report.PercentChanged:F2}%<br/>
                   <strong>Previous price:</strong> ${report.OldUsdPrice:F2}<br/>
                   <strong>Current price:</strong> ${report.NewUsdPrice:F2}</p>
                <div style="font-size:12px;color:#777">Compared at {report.NewPriceAtUtc:yyyy-MM-dd HH:mm} UTC</div>
              </div>
            </body></html>
            """;
    }
}

public sealed class EmailDeliveryChannel(EmailSender emailSender, IReportEmailTemplateRenderer templateRenderer) : IDeliveryChannel
{
    public string Name => "email";

    public Task DeliverAsync(DeliveryDocument delivery, CoinReportDocument report, CancellationToken cancellationToken)
    {
        return emailSender.SendAsync(delivery.Id, delivery.RecipientEmail, delivery.RecipientName, report,
            templateRenderer.Render(report), cancellationToken);
    }
}

public sealed class DiscordDeliveryChannel(MongoStore store, UserProfileService profiles, IHttpClientFactory clients) : IDeliveryChannel
{
    public string Name => "discord";

    public async Task DeliverAsync(DeliveryDocument delivery, CoinReportDocument report, CancellationToken cancellationToken)
    {
        if (delivery.UserId is null || delivery.DiscordEndpointId is null)
            throw new PermanentDeliveryException("Discord delivery is missing its destination reference.");
        var user = await store.Database.GetCollection<UserDocument>(MongoStore.UsersCollection)
            .Find(x => x.Id == delivery.UserId.Value).FirstOrDefaultAsync(cancellationToken);
        var endpoint = user?.DiscordEndpoints.FirstOrDefault(x => x.Id == delivery.DiscordEndpointId.Value);
        if (endpoint is null) throw new PermanentDeliveryException("Discord endpoint is no longer available.");

        var url = profiles.Decrypt(endpoint.EncryptedUrl);
        var builder = new UriBuilder(url) { Query = "wait=true" };
        var payload = new
        {
            embeds = new[] { new {
                title = $"{report.CoinName} ({report.CoinCode.ToUpperInvariant()}) price movement",
                description = $"Movement: {report.PercentChanged:F2}%\nPrevious: ${report.OldUsdPrice:F2} USD\nLatest: ${report.NewUsdPrice:F2} USD\nPeriod: {report.PeriodStartUtc:yyyy-MM-dd HH:mm}–{report.PeriodEndUtc:yyyy-MM-dd HH:mm} UTC",
                color = report.PercentChanged >= 0 ? 0x28A745 : 0xDC3545
            } }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, builder.Uri) { Content = System.Net.Http.Json.JsonContent.Create(payload) };
        using var response = await clients.CreateClient("discord").SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new PermanentDeliveryException($"Discord returned HTTP {(int)response.StatusCode}.");
    }
}

