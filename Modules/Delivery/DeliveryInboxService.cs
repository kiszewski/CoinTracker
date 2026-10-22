using CoinTracker.Infrastructure.Persistence;
using CoinTracker.Shared.Messaging;
using MongoDB.Driver;

namespace CoinTracker.Modules.Delivery;

public sealed class DeliveryInboxService(MongoStore store, ILogger<DeliveryInboxService> logger)
{
    private const string ConsumerName = "delivery-inbox";

    public async Task HandleAsync(EventEnvelope<ReportCreatedPayload> message, CancellationToken cancellationToken)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = message.CorrelationId,
            ["EventId"] = message.EventId,
            ["ReportId"] = message.Payload.ReportId
        });
        var processedId = $"{ConsumerName}:{message.EventId:N}";
        var processed = store.Database.GetCollection<ProcessedMessageDocument>(MongoStore.ProcessedCollection);
        if (await processed.Find(x => x.Id == processedId).AnyAsync(cancellationToken))
            return;

        var session = await store.Client.StartSessionAsync(cancellationToken: cancellationToken);
        using (session)
        {
            session.StartTransaction();
            try
            {
                if (!await processed.Find(session, x => x.Id == processedId).AnyAsync(cancellationToken))
                {
                    var recipient = InitialRecipient.Instance;
                    if (recipient.FavoriteCoins.Contains(message.Payload.CoinCode, StringComparer.OrdinalIgnoreCase))
                    {
                        var deliveryId = $"{message.Payload.ReportId:N}:{recipient.Email.ToLowerInvariant()}:email";
                        var deliveries = store.Database.GetCollection<DeliveryDocument>(MongoStore.DeliveriesCollection);
                        if (!await deliveries.Find(session, x => x.Id == deliveryId).AnyAsync(cancellationToken))
                        {
                            await deliveries.InsertOneAsync(session, new DeliveryDocument
                            {
                                Id = deliveryId,
                                ReportId = message.Payload.ReportId,
                                CorrelationId = message.CorrelationId,
                                RecipientEmail = recipient.Email,
                                RecipientName = recipient.Name,
                                Channel = "email",
                                Status = "pending",
                                Attempts = 0,
                                NextAttemptAtUtc = DateTime.UtcNow,
                                CreatedAtUtc = DateTime.UtcNow
                            }, cancellationToken: cancellationToken);
                        }
                    }

                    await processed.InsertOneAsync(session, new ProcessedMessageDocument
                    {
                        Id = processedId,
                        Consumer = ConsumerName,
                        EventId = message.EventId,
                        ProcessedAtUtc = DateTime.UtcNow
                    }, cancellationToken: cancellationToken);
                }

                await session.CommitTransactionAsync(cancellationToken);
            }
            catch
            {
                await session.AbortTransactionAsync(cancellationToken);
                throw;
            }
        }

        logger.LogInformation("Processed report event {ReportId} for delivery", message.Payload.ReportId);
    }
}

internal sealed class InitialRecipient
{
    public static InitialRecipient Instance { get; } = new();
    public string Name => "Leonardo";
    public string Email => "kiszewski1999@gmail.com";
    public IReadOnlyList<string> FavoriteCoins => ["bitcoin"];
}
