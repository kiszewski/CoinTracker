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
                    var audience = store.Database.GetCollection<ReportAudienceDocument>(MongoStore.ReportAudiencesCollection);
                    var userIds = await audience.Find(session, x => x.ReportId == message.Payload.ReportId).Project(x => x.UserId).ToListAsync(cancellationToken);
                    var users = store.Database.GetCollection<UserDocument>(MongoStore.UsersCollection);
                    var deliveries = store.Database.GetCollection<DeliveryDocument>(MongoStore.DeliveriesCollection);
                    foreach (var userId in userIds)
                    {
                        var user = await users.Find(session, x => x.Id == userId).FirstOrDefaultAsync(cancellationToken);
                        var now = DateTime.UtcNow;
                        if (user is null)
                        {
                            await deliveries.UpdateOneAsync(session, x => x.Id == $"{message.Payload.ReportId:N}:{userId:N}:email", Builders<DeliveryDocument>.Update
                                .SetOnInsert(x => x.ReportId, message.Payload.ReportId).SetOnInsert(x => x.CorrelationId, message.CorrelationId)
                                .SetOnInsert(x => x.UserId, userId).SetOnInsert(x => x.Channel, "email").SetOnInsert(x => x.Status, "unavailable")
                                .SetOnInsert(x => x.LastError, "Qualified recipient profile is unavailable.").SetOnInsert(x => x.Attempts, 0)
                                .SetOnInsert(x => x.NextAttemptAtUtc, now).SetOnInsert(x => x.CreatedAtUtc, now), new UpdateOptions { IsUpsert = true }, cancellationToken);
                            continue;
                        }
                        await deliveries.UpdateOneAsync(session, x => x.Id == $"{message.Payload.ReportId:N}:{user.Id:N}:email", Builders<DeliveryDocument>.Update
                            .SetOnInsert(x => x.ReportId, message.Payload.ReportId).SetOnInsert(x => x.CorrelationId, message.CorrelationId)
                            .SetOnInsert(x => x.UserId, user.Id).SetOnInsert(x => x.RecipientEmail, user.Email).SetOnInsert(x => x.RecipientName, user.Name)
                            .SetOnInsert(x => x.Channel, "email").SetOnInsert(x => x.Status, "pending").SetOnInsert(x => x.Attempts, 0)
                            .SetOnInsert(x => x.NextAttemptAtUtc, now).SetOnInsert(x => x.CreatedAtUtc, now), new UpdateOptions { IsUpsert = true }, cancellationToken);
                        foreach (var endpoint in user.DiscordEndpoints)
                            await deliveries.UpdateOneAsync(session, x => x.Id == $"{message.Payload.ReportId:N}:{user.Id:N}:discord:{endpoint.Id:N}", Builders<DeliveryDocument>.Update
                                .SetOnInsert(x => x.ReportId, message.Payload.ReportId).SetOnInsert(x => x.CorrelationId, message.CorrelationId)
                                .SetOnInsert(x => x.UserId, user.Id).SetOnInsert(x => x.DiscordEndpointId, endpoint.Id)
                                .SetOnInsert(x => x.RecipientName, user.Name).SetOnInsert(x => x.Channel, "discord").SetOnInsert(x => x.Status, "pending")
                                .SetOnInsert(x => x.Attempts, 0).SetOnInsert(x => x.NextAttemptAtUtc, now).SetOnInsert(x => x.CreatedAtUtc, now), new UpdateOptions { IsUpsert = true }, cancellationToken);
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
