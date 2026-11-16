using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace CoinTracker.Shared.Observability;

public static class MetricsConfiguration
{
    public static void AddConsumerMetrics(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var endpoint = OtlpConfiguration.GetBaseEndpoint(configuration);
        if (endpoint is null) return;

        var serviceName = configuration["OTEL_SERVICE_NAME"];
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(string.IsNullOrWhiteSpace(serviceName) ? "cointracker-api" : serviceName)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = environment.EnvironmentName
                }))
            .WithMetrics(metrics => metrics
                .AddMeter(CoinTrackerMetrics.ConsumerMeterName)
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(endpoint, "v1/metrics");
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Headers = configuration["OTEL_EXPORTER_OTLP_HEADERS"];
                }));
    }
}
