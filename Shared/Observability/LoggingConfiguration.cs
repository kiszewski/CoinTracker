using Serilog;
using Serilog.Events;
using Serilog.Sinks.OpenTelemetry;

namespace CoinTracker.Shared.Observability;

public static class LoggingConfiguration
{
    public const string ConsoleTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}";

    public static void Configure(LoggerConfiguration logger, IConfiguration configuration, IHostEnvironment environment)
    {
        logger.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: ConsoleTemplate);

        var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                "OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute HTTP(S) base URL without credentials, query, or fragment.");

        var protocol = configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];
        if (!string.IsNullOrWhiteSpace(protocol) && protocol != "http/protobuf")
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_PROTOCOL must be http/protobuf.");

        try
        {
            logger.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = endpoint;
                options.Protocol = OtlpProtocol.HttpProtobuf;
                options.IncludedData |= IncludedData.SourceContextAttribute;
                options.ResourceAttributes["service.name"] = "cointracker-api";
                options.ResourceAttributes["deployment.environment.name"] = environment.EnvironmentName;
                options.BatchingOptions.QueueLimit = 10_000;
            }, getConfigurationVariable: name => configuration[name]);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            // Header parsing errors can contain secret values; never include the original exception.
            throw new InvalidOperationException("Invalid OTLP logging configuration. Check OTEL_EXPORTER_OTLP_HEADERS and OTEL_RESOURCE_ATTRIBUTES formatting.");
        }
    }
}
