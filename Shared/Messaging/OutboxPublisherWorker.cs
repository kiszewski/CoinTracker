using CoinTracker.Infrastructure.Configuration;
using CoinTracker.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using RabbitMQ.Client;
using System.Text;
using CoinTracker.Shared.Observability;

namespace CoinTracker.Shared.Messaging;

public sealed class OutboxPublisherWorker(
    MongoStore store,
    RabbitMqConnectionFactory connectionFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private readonly string _owner = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = connectionFactory.CreateConnection();
                using var channel = connection.CreateModel();
                channel.ExchangeDeclare(options.Value.ExchangeName, ExchangeType.Topic, durable: true);
                channel.ConfirmSelect();

                while (!stoppingToken.IsCancellationRequested)
                {
                    if (await AcquireLeaseAsync(stoppingToken))
                    {
                        var publishedCount = await PublishPendingAsync(channel, stoppingToken);
                        if (publishedCount == 0)
                            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox publisher connection failed; retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var leases = store.Database.GetCollection<OutboxPublishLeaseDocument>(MongoStore.LeasesCollection);
        var filter = Builders<OutboxPublishLeaseDocument>.Filter.And(
            Builders<OutboxPublishLeaseDocument>.Filter.Eq(x => x.Id, "outbox-publisher"),
            Builders<OutboxPublishLeaseDocument>.Filter.Or(
                Builders<OutboxPublishLeaseDocument>.Filter.Lt(x => x.ExpiresAtUtc, now),
                Builders<OutboxPublishLeaseDocument>.Filter.Eq(x => x.Owner, _owner)));
        var update = Builders<OutboxPublishLeaseDocument>.Update
            .SetOnInsert(x => x.Id, "outbox-publisher")
            .Set(x => x.Owner, _owner)
            .Set(x => x.ExpiresAtUtc, now.AddSeconds(20));

        try
        {
            var lease = await leases.FindOneAndUpdateAsync(filter, update,
                new FindOneAndUpdateOptions<OutboxPublishLeaseDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, cancellationToken);
            return lease?.Owner == _owner;
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
        catch (MongoCommandException exception) when (exception.Code == 11000)
        {
            // findAndModify reports a competing upsert against the lease _id as a command error.
            return false;
        }
    }

    private async Task<int> PublishPendingAsync(IModel channel, CancellationToken cancellationToken)
    {
        var collection = store.Database.GetCollection<OutboxMessageDocument>(MongoStore.OutboxCollection);
        var filter = Builders<OutboxMessageDocument>.Filter.Eq(x => x.PublishedAtUtc, null);
        var pending = await collection.Find(filter).SortBy(x => x.CreatedAtUtc).Limit(50).ToListAsync(cancellationToken);
        var published = 0;

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;
                properties.ContentType = "application/json";
                properties.MessageId = message.Id.ToString();
                properties.CorrelationId = message.CorrelationId.ToString();
                properties.Headers = new Dictionary<string, object>
                {
                    ["event-type"] = message.EventType,
                    ["event-id"] = message.Id.ToString(),
                    ["correlation-id"] = message.CorrelationId.ToString()
                };

                channel.BasicPublish(options.Value.ExchangeName, message.EventType, mandatory: true, properties, Encoding.UTF8.GetBytes(message.PayloadJson));
                channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));

                var update = Builders<OutboxMessageDocument>.Update
                    .Set(x => x.PublishedAtUtc, DateTime.UtcNow)
                    .Inc(x => x.PublishAttempts, 1);
                await collection.UpdateOneAsync(x => x.Id == message.Id && x.PublishedAtUtc == null, update, cancellationToken: cancellationToken);
                CoinTrackerMetrics.EventPublished(message.EventType);
                published++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed publishing outbox event {EventId} ({EventType})", message.Id, message.EventType);
                await collection.UpdateOneAsync(x => x.Id == message.Id && x.PublishedAtUtc == null,
                    Builders<OutboxMessageDocument>.Update.Inc(x => x.PublishAttempts, 1), cancellationToken: cancellationToken);
                break;
            }
        }

        return published;
    }
}
