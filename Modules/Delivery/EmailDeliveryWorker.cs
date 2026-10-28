using CoinTracker.Infrastructure.Configuration;
using CoinTracker.Infrastructure.Persistence;
using CoinTracker.Shared.Observability;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using MongoDB.Driver;

namespace CoinTracker.Modules.Delivery;

public sealed class PermanentDeliveryException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class EmailSender(IOptions<EmailOptions> options)
{
    public async Task SendAsync(string deliveryId, string recipientEmail, string recipientName, CoinReportDocument report, string html, CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (string.IsNullOrWhiteSpace(configuration.ApiKey) || string.IsNullOrWhiteSpace(configuration.SenderEmail) || string.IsNullOrWhiteSpace(configuration.SenderPassword))
            throw new PermanentDeliveryException("MailJet email configuration is incomplete.");

        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(configuration.SenderName, configuration.SenderEmail));
        message.To.Add(new MailboxAddress(recipientName, recipientEmail));
        message.Subject = $"Daily price movement: {report.CoinName}";
        message.MessageId = $"{deliveryId.Replace(':', '-')}@cointracker.local";
        message.Body = new TextPart(TextFormat.Html) { Text = html };

        using var client = new MailKit.Net.Smtp.SmtpClient();
        try
        {
            await client.ConnectAsync("in-v3.mailjet.com", 587, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(configuration.ApiKey, configuration.SenderPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (MailKit.Net.Smtp.SmtpCommandException exception) when ((int)exception.StatusCode >= 500)
        {
            throw new PermanentDeliveryException("The email provider rejected the delivery permanently.", exception);
        }
        catch (Exception exception) when (exception is not PermanentDeliveryException)
        {
            throw new IOException("Temporary email delivery failure.", exception);
        }
    }
}

public sealed class EmailDeliveryWorker(
    MongoStore store,
    IEnumerable<IDeliveryChannel> channels,
    IOptions<RabbitMqOptions> rabbitOptions,
    ILogger<EmailDeliveryWorker> logger) : BackgroundService
{
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly IDictionary<string, IDeliveryChannel> _channels = channels.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delivery = await ClaimNextAsync(stoppingToken);
                if (delivery is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                await ProcessAsync(delivery, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Email delivery worker encountered an error");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<DeliveryDocument?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var collection = store.Database.GetCollection<DeliveryDocument>(MongoStore.DeliveriesCollection);
        await collection.UpdateManyAsync(x => x.Channel == "discord" && x.Status == "processing" && x.LeaseExpiresAtUtc <= now,
            Builders<DeliveryDocument>.Update.Set(x => x.Status, "unknown").Set(x => x.LastError, "Attempt outcome unknown after worker interruption.").Set(x => x.LeaseExpiresAtUtc, null).Set(x => x.LeaseOwner, null), cancellationToken: cancellationToken);
        var filter = Builders<DeliveryDocument>.Filter.Or(
            Builders<DeliveryDocument>.Filter.And(
                Builders<DeliveryDocument>.Filter.Eq(x => x.Status, "pending"),
                Builders<DeliveryDocument>.Filter.Or(Builders<DeliveryDocument>.Filter.Eq(x => x.Channel, "email"), Builders<DeliveryDocument>.Filter.Eq(x => x.Attempts, 0)),
                Builders<DeliveryDocument>.Filter.Lte(x => x.NextAttemptAtUtc, now)),
            Builders<DeliveryDocument>.Filter.And(
                Builders<DeliveryDocument>.Filter.Eq(x => x.Channel, "email"),
                Builders<DeliveryDocument>.Filter.Eq(x => x.Status, "processing"),
                Builders<DeliveryDocument>.Filter.Lte(x => x.LeaseExpiresAtUtc, now)));
        var update = Builders<DeliveryDocument>.Update
            .Set(x => x.Status, "processing")
            .Set(x => x.LeaseExpiresAtUtc, now.AddMinutes(2))
            .Set(x => x.LeaseOwner, _owner)
            .Inc(x => x.Attempts, 1);
        return await collection.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<DeliveryDocument>
            {
                Sort = Builders<DeliveryDocument>.Sort.Ascending(x => x.NextAttemptAtUtc),
                ReturnDocument = ReturnDocument.After
            }, cancellationToken);
    }

    private async Task ProcessAsync(DeliveryDocument delivery, CancellationToken cancellationToken)
    {
        var reports = store.Database.GetCollection<CoinReportDocument>(MongoStore.ReportsCollection);
        var report = await reports.Find(x => x.Id == delivery.ReportId).FirstOrDefaultAsync(cancellationToken);
        if (report is null)
        {
            await MarkFailedAsync(delivery, "Report record not found.", cancellationToken);
            return;
        }

        try
        {
            using var logScope = logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = delivery.CorrelationId,
                ["DeliveryId"] = delivery.Id,
                ["ReportId"] = delivery.ReportId
            });
            if (!_channels.TryGetValue(delivery.Channel, out var channel))
                throw new PermanentDeliveryException($"Delivery channel '{delivery.Channel}' is not registered.");

            await channel.DeliverAsync(delivery, report, cancellationToken);
            var update = Builders<DeliveryDocument>.Update
                .Set(x => x.Status, "delivered")
                .Set(x => x.DeliveredAtUtc, DateTime.UtcNow)
                .Set(x => x.LeaseExpiresAtUtc, null)
                .Set(x => x.LeaseOwner, null)
                .Set(x => x.LastError, null);
            await store.Database.GetCollection<DeliveryDocument>(MongoStore.DeliveriesCollection)
                .UpdateOneAsync(x => x.Id == delivery.Id && x.Status == "processing" && x.LeaseOwner == _owner, update, cancellationToken: cancellationToken);
            CoinTrackerMetrics.DeliveryCompleted(delivery.Channel, "delivered");
            logger.LogInformation("Delivered report {ReportId} through {Channel}", delivery.ReportId, delivery.Channel);
        }
        catch (PermanentDeliveryException exception)
        {
            await MarkFailedAsync(delivery, exception.Message, cancellationToken);
            CoinTrackerMetrics.DeliveryCompleted(delivery.Channel, "failed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.Database.GetCollection<DeliveryDocument>(MongoStore.DeliveriesCollection)
                .UpdateOneAsync(x => x.Id == delivery.Id && x.Status == "processing" && x.LeaseOwner == _owner,
                    Builders<DeliveryDocument>.Update.Set(x => x.Status, delivery.Channel == "discord" ? "unknown" : "pending").Set(x => x.NextAttemptAtUtc, DateTime.UtcNow).Set(x => x.LeaseExpiresAtUtc, null).Set(x => x.LeaseOwner, null),
                    cancellationToken: CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            if (delivery.Channel == "discord")
            {
                await MarkFailedAsync(delivery, exception.GetType().Name, cancellationToken);
                CoinTrackerMetrics.DeliveryCompleted(delivery.Channel, "failed");
                logger.LogWarning("Discord delivery {DeliveryId} failed; it will not be retried", delivery.Id);
                return;
            }
            var attemptsAllowed = rabbitOptions.Value.RetryCount + 1;
            if (delivery.Attempts < attemptsAllowed)
            {
                var retryAt = DateTime.UtcNow.AddSeconds(rabbitOptions.Value.RetryIntervalSeconds);
                await store.Database.GetCollection<DeliveryDocument>(MongoStore.DeliveriesCollection)
                    .UpdateOneAsync(x => x.Id == delivery.Id && x.Status == "processing" && x.LeaseOwner == _owner,
                        Builders<DeliveryDocument>.Update
                            .Set(x => x.Status, "pending")
                            .Set(x => x.NextAttemptAtUtc, retryAt)
                            .Set(x => x.LeaseExpiresAtUtc, null)
                            .Set(x => x.LeaseOwner, null)
                            .Set(x => x.LastError, exception.GetType().Name),
                        cancellationToken: cancellationToken);
                CoinTrackerMetrics.DeliveryRetried(delivery.Channel);
                logger.LogWarning(exception, "Email delivery {DeliveryId} will retry at {RetryAtUtc}; attempt {Attempt}/{AttemptsAllowed}", delivery.Id, retryAt, delivery.Attempts, attemptsAllowed);
            }
            else
            {
                await MarkFailedAsync(delivery, exception.GetType().Name, cancellationToken);
                CoinTrackerMetrics.DeliveryCompleted(delivery.Channel, "failed");
                logger.LogError(exception, "Email delivery {DeliveryId} exhausted its retry attempts", delivery.Id);
            }
        }
    }

    private Task MarkFailedAsync(DeliveryDocument delivery, string reason, CancellationToken cancellationToken)
    {
        return store.Database.GetCollection<DeliveryDocument>(MongoStore.DeliveriesCollection)
            .UpdateOneAsync(x => x.Id == delivery.Id && x.Status == "processing" && x.LeaseOwner == _owner,
                Builders<DeliveryDocument>.Update
                    .Set(x => x.Status, "failed")
                    .Set(x => x.LeaseExpiresAtUtc, null)
                    .Set(x => x.LeaseOwner, null)
                    .Set(x => x.LastError, reason),
                cancellationToken: cancellationToken);
    }
}
