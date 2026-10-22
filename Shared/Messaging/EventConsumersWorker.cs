using System.Text;
using System.Text.Json;
using CoinTracker.Infrastructure.Configuration;
using CoinTracker.Modules.Delivery;
using CoinTracker.Modules.Reporting;
using CoinTracker.Shared.Observability;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CoinTracker.Shared.Messaging;

public sealed class EventConsumersWorker(
    RabbitMqConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<EventConsumersWorker> logger) : BackgroundService
{
    private const string ReportingQueue = "cointracker.reporting.market-data-populated";
    private const string DeliveryQueue = "cointracker.delivery.report-created";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = connectionFactory.CreateConnection();
                using var reportingChannel = ConfigureQueue(connection, ReportingQueue, "market-data.populated.v1");
                using var deliveryChannel = ConfigureQueue(connection, DeliveryQueue, "report.created.v1");

                AttachConsumer<MarketDataPopulatedPayload>(reportingChannel, ReportingQueue, "market-data.populated.v1", async (envelope, token) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ReportAnalysisService>().HandleAsync(envelope, token);
                });
                AttachConsumer<ReportCreatedPayload>(deliveryChannel, DeliveryQueue, "report.created.v1", async (envelope, token) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<DeliveryInboxService>().HandleAsync(envelope, token);
                });

                logger.LogInformation("RabbitMQ consumers are listening on {ReportingQueue} and {DeliveryQueue}", ReportingQueue, DeliveryQueue);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RabbitMQ consumer connection failed; retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private IModel ConfigureQueue(IConnection connection, string queue, string eventType)
    {
        var channel = connection.CreateModel();
        channel.ExchangeDeclare(options.Value.ExchangeName, ExchangeType.Topic, durable: true);
        channel.ExchangeDeclare(options.Value.DeadLetterExchangeName, ExchangeType.Topic, durable: true);

        var deadQueue = $"{queue}.dead";
        channel.QueueDeclare(deadQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(deadQueue, options.Value.DeadLetterExchangeName, queue);
        channel.QueueDeclare(queue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(queue, options.Value.ExchangeName, eventType);

        var retryQueue = $"{queue}.retry";
        var retryRoutingKey = $"retry.{eventType}";
        var retryArgs = new Dictionary<string, object>
        {
            ["x-message-ttl"] = options.Value.RetryIntervalSeconds * 1000,
            ["x-dead-letter-exchange"] = options.Value.ExchangeName,
            ["x-dead-letter-routing-key"] = eventType
        };
        channel.QueueDeclare(retryQueue, durable: true, exclusive: false, autoDelete: false, arguments: retryArgs);
        channel.QueueBind(retryQueue, options.Value.ExchangeName, retryRoutingKey);
        channel.BasicQos(0, 1, global: false);
        channel.ConfirmSelect();
        return channel;
    }

    private void AttachConsumer<TPayload>(IModel channel, string queue, string eventType,
        Func<EventEnvelope<TPayload>, CancellationToken, Task> handler)
    {
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (_, args) =>
        {
            var cancellationToken = stoppingToken;
            Guid eventId = Guid.Empty;
            try
            {
                var json = Encoding.UTF8.GetString(args.Body.ToArray());
                var envelope = JsonSerializer.Deserialize<EventEnvelope<TPayload>>(json, JsonOptions)
                    ?? throw new InvalidDataException($"Could not deserialize {eventType} event.");
                eventId = envelope.EventId;
                using var logScope = logger.BeginScope(new Dictionary<string, object>
                {
                    ["CorrelationId"] = envelope.CorrelationId,
                    ["EventId"] = envelope.EventId,
                    ["ConsumerQueue"] = queue
                });
                await handler(envelope, cancellationToken);
                CoinTrackerMetrics.EventHandled(queue, "succeeded");
                channel.BasicAck(args.DeliveryTag, multiple: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                channel.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
            }
            catch (Exception exception)
            {
                var attempt = exception is InvalidDataException ? options.Value.RetryCount : ReadRetryCount(args.BasicProperties.Headers);
                CoinTrackerMetrics.EventHandled(queue, "failed");
                logger.LogError(exception, "Consumer {Queue} failed handling {EventType} event {EventId}, attempt {Attempt}", queue, eventType, eventId, attempt + 1);
                try
                {
                    if (attempt < options.Value.RetryCount)
                    {
                        CoinTrackerMetrics.EventHandled(queue, "retrying");
                        var retryProperties = channel.CreateBasicProperties();
                        retryProperties.Persistent = true;
                        retryProperties.ContentType = args.BasicProperties.ContentType;
                        retryProperties.MessageId = args.BasicProperties.MessageId;
                        retryProperties.CorrelationId = args.BasicProperties.CorrelationId;
                        retryProperties.Headers = args.BasicProperties.Headers is null
                            ? new Dictionary<string, object>()
                            : new Dictionary<string, object>(args.BasicProperties.Headers);
                        retryProperties.Headers["x-retry-count"] = attempt + 1;
                        channel.BasicPublish(options.Value.ExchangeName, $"retry.{eventType}", mandatory: true, retryProperties, args.Body);
                        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));
                    }
                    else
                    {
                        var deadProperties = channel.CreateBasicProperties();
                        deadProperties.Persistent = true;
                        deadProperties.ContentType = args.BasicProperties.ContentType;
                        deadProperties.MessageId = args.BasicProperties.MessageId;
                        deadProperties.CorrelationId = args.BasicProperties.CorrelationId;
                        deadProperties.Headers = args.BasicProperties.Headers is null
                            ? new Dictionary<string, object>()
                            : new Dictionary<string, object>(args.BasicProperties.Headers);
                        deadProperties.Headers["x-retry-count"] = attempt;
                        deadProperties.Headers["x-dead-letter-reason"] = exception.GetType().Name;
                        channel.BasicPublish(options.Value.DeadLetterExchangeName, queue, mandatory: true, deadProperties, args.Body);
                        channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));
                    }

                    channel.BasicAck(args.DeliveryTag, multiple: false);
                }
                catch (Exception publishException)
                {
                    logger.LogError(publishException, "Could not route failed event {EventId}; requeueing", eventId);
                    channel.BasicNack(args.DeliveryTag, multiple: false, requeue: true);
                }
            }
        };

        channel.BasicConsume(queue, autoAck: false, consumer);
    }

    private static int ReadRetryCount(IDictionary<string, object>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-retry-count", out var value))
            return 0;
        if (value is byte[] bytes && int.TryParse(Encoding.UTF8.GetString(bytes), out var numberFromBytes))
            return numberFromBytes;
        return value switch
        {
            int number => number,
            long number => (int)number,
            _ when int.TryParse(value?.ToString(), out var parsedText) => parsedText,
            _ => 0
        };
    }

    private CancellationToken stoppingToken => _stoppingToken;
    private CancellationToken _stoppingToken;
}
