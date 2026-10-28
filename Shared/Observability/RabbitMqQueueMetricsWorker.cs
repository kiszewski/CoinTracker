using CoinTracker.Shared.Messaging;
using RabbitMQ.Client;

namespace CoinTracker.Shared.Observability;

public sealed class RabbitMqQueueMetricsWorker(
    RabbitMqConnectionFactory connectionFactory,
    ILogger<RabbitMqQueueMetricsWorker> logger) : BackgroundService
{
    private static readonly string[] Queues =
    [
        "cointracker.reporting.market-data-populated",
        "cointracker.reporting.market-data-populated.retry",
        "cointracker.reporting.market-data-populated.dead",
        "cointracker.delivery.report-created",
        "cointracker.delivery.report-created.retry",
        "cointracker.delivery.report-created.dead"
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = connectionFactory.CreateConnection();
                using var channel = connection.CreateModel();
                foreach (var queue in Queues)
                {
                    try
                    {
                        var result = channel.QueueDeclarePassive(queue);
                        CoinTrackerMetrics.SetRabbitQueueDepth(queue, result.MessageCount);
                    }
                    catch (Exception exception)
                    {
                        logger.LogDebug(exception, "Could not read queue depth for {QueueName}", queue);
                    }
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not collect RabbitMQ queue metrics");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
