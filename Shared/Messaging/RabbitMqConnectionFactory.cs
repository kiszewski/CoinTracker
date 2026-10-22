using CoinTracker.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace CoinTracker.Shared.Messaging;

public sealed class RabbitMqConnectionFactory(IOptions<RabbitMqOptions> options)
{
    public IConnection CreateConnection()
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.Value.ConnectionString),
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
        };

        return factory.CreateConnection("cointracker-api");
    }
}
