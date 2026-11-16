using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CoinTracker.Shared.Observability;

public static class TelemetryConfiguration
{
    public static void AddApplicationTelemetry(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
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
                .AddOtlpExporter("metrics", options =>
                {
                    options.Endpoint = new Uri(endpoint, "v1/metrics");
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Headers = configuration["OTEL_EXPORTER_OTLP_HEADERS"];
                }))
            .WithTracing(traces => traces
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddProcessor(new HttpTraceRedactionProcessor())
                .AddOtlpExporter("traces", options =>
                {
                    options.Endpoint = new Uri(endpoint, "v1/traces");
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Headers = configuration["OTEL_EXPORTER_OTLP_HEADERS"];
                }));
    }
}
