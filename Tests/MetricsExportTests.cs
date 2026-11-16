using System.Net;
using System.Text;
using CoinTracker.Shared.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using Xunit;

namespace CoinTracker.Tests;

public sealed class MetricsExportTests
{
    [Theory]
    [InlineData("/otlp")]
    [InlineData("/otlp/v1/logs")]
    [InlineData("/otlp/v1/metrics")]
    public void ExportsConsumerMetricsWithSharedEndpointCredentialsAndResource(string path)
    {
        var builder = CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://collector.invalid" + path,
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "Authorization=Basic%20test-credential",
            ["OTEL_SERVICE_NAME"] = "metrics-test"
        });
        using var handler = new CaptureHandler();
        builder.Services.AddConsumerMetrics(builder.Configuration, builder.Environment);
        builder.Services.Configure<OtlpExporterOptions>(options =>
            options.HttpClientFactory = () => new HttpClient(handler, disposeHandler: false));
        using var host = builder.Build();
        var provider = host.Services.GetRequiredService<MeterProvider>();
        var queue = $"export-{Guid.NewGuid()}";
        CoinTrackerMetrics.TrackConsumer(queue, () => true);
        CoinTrackerMetrics.EventHandled(queue, "succeeded");
        CoinTrackerMetrics.EventHandled(queue, "failed");

        Assert.True(provider.ForceFlush());
        Assert.Equal("https://collector.invalid/otlp/v1/metrics", handler.Endpoint);
        Assert.Equal("Basic test-credential", handler.Authorization);
        Assert.Equal("application/x-protobuf", handler.ContentType);
        foreach (var expected in new[]
        {
            "cointracker.consumer.messages.processed", "cointracker.consumer.processing.failures",
            "cointracker.consumer.active", "queue", queue, "service.name", "metrics-test",
            "deployment.environment.name", "MetricsTest", "service.instance.id"
        })
            Assert.Contains(expected, handler.Payload);
        Assert.DoesNotContain("cointracker.events.handled", handler.Payload);
    }

    [Fact]
    public void DoesNotRegisterExporterWithoutEndpoint()
    {
        var builder = CreateBuilder();
        builder.Services.AddConsumerMetrics(builder.Configuration, builder.Environment);
        using var host = builder.Build();
        Assert.Null(host.Services.GetService<MeterProvider>());
    }

    [Fact]
    public void ExportFailureDoesNotThrowIntoMessageProcessing()
    {
        var builder = CreateBuilder();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://collector.invalid/otlp";
        using var handler = new CaptureHandler { StatusCode = HttpStatusCode.Unauthorized };
        builder.Services.AddConsumerMetrics(builder.Configuration, builder.Environment);
        builder.Services.Configure<OtlpExporterOptions>(options =>
            options.HttpClientFactory = () => new HttpClient(handler, disposeHandler: false));
        using var host = builder.Build();
        var provider = host.Services.GetRequiredService<MeterProvider>();
        var queue = $"failure-{Guid.NewGuid()}";
        CoinTrackerMetrics.TrackConsumer(queue, () => true);
        CoinTrackerMetrics.EventHandled(queue, "succeeded");
        provider.ForceFlush();
        Assert.NotEmpty(handler.Payload);
        CoinTrackerMetrics.EventHandled(queue, "succeeded");
    }

    private static HostApplicationBuilder CreateBuilder() => Host.CreateApplicationBuilder(
        new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "MetricsTest" });

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public string? Endpoint { get; private set; }
        public string? Authorization { get; private set; }
        public string? ContentType { get; private set; }
        public string Payload { get; private set; } = "";

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.SingleOrDefault() : null;
            ContentType = request.Content!.Headers.ContentType!.MediaType;
            Payload = Encoding.UTF8.GetString(request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult());
            return new HttpResponseMessage(StatusCode) { Content = new ByteArrayContent([]) };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
