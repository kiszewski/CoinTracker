using CoinTracker.Infrastructure.Persistence;
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

