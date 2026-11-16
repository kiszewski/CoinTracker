using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CoinTracker.Shared.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using Serilog;
using Xunit;

namespace CoinTracker.Tests;

public sealed class HttpTracingTests
{
    [Fact]
    public async Task TracesIncomingAndOutgoingRequestsWithPropagationErrorsAndRedaction()
    {
        using var spans = new CaptureExporter();
        using var collector = new CaptureHandler();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        Configure(builder.Services, builder.Configuration, builder.Environment, spans, collector);
        builder.Services.AddHttpClient();
        await using var app = builder.Build();
        string address = "";
        app.MapGet("/dependency", () => Results.Ok());
        app.MapGet("/failure", () => Results.StatusCode(500));
        app.MapGet("/api/webhooks/{id}/{token}", () => Results.NoContent());
        app.MapGet("/forward", async (IHttpClientFactory clients) =>
        {
            using var client = clients.CreateClient();
            using var dependency = await client.GetAsync(address + "/dependency?api_key=query-secret");
            using var webhook = await client.GetAsync(address + "/api/webhooks/123/webhook-secret?token=query-secret");
            using var failure = await client.GetAsync(address + "/failure");
            return Results.Ok();
        });
        await app.StartAsync();
        address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        const string traceId = "12345678901234567890123456789012";
        const string parentId = "1234567890123456";
        using (SuppressInstrumentationScope.Begin())
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/forward");
            request.Headers.Add("traceparent", $"00-{traceId}-{parentId}-01");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var provider = app.Services.GetRequiredService<TracerProvider>();
        Assert.True(provider.ForceFlush());
        var recorded = spans.Activities.ToArray();
        var server = Assert.Single(recorded, a => a.Kind == ActivityKind.Server && Equals(a.GetTagItem("http.route"), "/forward"));
        Assert.Equal(traceId, server.TraceId.ToHexString());
        Assert.Equal(parentId, server.ParentSpanId.ToHexString());
        var outgoing = recorded.Where(a => a.Kind == ActivityKind.Client).ToArray();
        Assert.Equal(3, outgoing.Length);
        Assert.All(outgoing, a =>
        {
            Assert.Equal(server.TraceId, a.TraceId);
            Assert.Equal(server.SpanId, a.ParentSpanId);
            Assert.Equal("GET", a.GetTagItem("http.request.method"));
        });
        Assert.Contains(outgoing, a => a.Status == ActivityStatusCode.Error && Convert.ToInt32(a.GetTagItem("http.response.status_code")) == 500);
        foreach (var activity in recorded)
        {
            Assert.Equal(server.TraceId, activity.TraceId);
            foreach (var tag in activity.TagObjects)
            {
                Assert.DoesNotContain("webhook-secret", tag.Value?.ToString() ?? "");
                Assert.DoesNotContain("query-secret", tag.Value?.ToString() ?? "");
            }
        }
        Assert.Contains(outgoing, a => a.GetTagItem("url.full")?.ToString()?.EndsWith("/webhooks/[redacted]") == true);
        Assert.Equal("https://collector.invalid/otlp/v1/traces", collector.Endpoint);
        Assert.Equal("Basic test-credential", collector.Authorization);
        Assert.Contains("http.request.method", collector.Payload);
        Assert.Contains("tracing-test", collector.Payload);
        Assert.DoesNotContain("webhook-secret", collector.Payload);
        Assert.DoesNotContain("query-secret", collector.Payload);
        await app.StopAsync();
    }

    [Fact]
    public async Task SerilogExportDoesNotCreateHttpClientSpans()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        using var spans = new CaptureExporter();
        using var collector = new CaptureHandler();
        Configure(builder.Services, builder.Configuration, builder.Environment, spans, collector);
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"http://127.0.0.1:{port}/otlp";
        using var host = builder.Build();
        await host.StartAsync();
        var loggerConfiguration = new LoggerConfiguration();
        LoggingConfiguration.Configure(loggerConfiguration, builder.Configuration, builder.Environment);
        using var logger = loggerConfiguration.CreateLogger();
        logger.Information("Logging suppression test");
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("/otlp/v1/logs", context.Request.Url!.AbsolutePath);
        await context.Request.InputStream.CopyToAsync(Stream.Null);
        context.Response.ContentLength64 = 0;
        context.Response.Close();
        logger.Dispose();
        Assert.True(host.Services.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.Empty(spans.Activities);
        await host.StopAsync();
    }

    private static void Configure(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment,
        CaptureExporter exporter, CaptureHandler handler)
    {
        configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://collector.invalid/otlp/v1/traces";
        configuration["OTEL_EXPORTER_OTLP_HEADERS"] = "Authorization=Basic%20test-credential";
        configuration["OTEL_SERVICE_NAME"] = "tracing-test";
        services.AddApplicationTelemetry(configuration, environment);
        foreach (var name in new[] { "metrics", "traces" })
            services.Configure<OtlpExporterOptions>(name, options =>
                options.HttpClientFactory = () => new HttpClient(handler, disposeHandler: false));
        services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
            tracing.AddProcessor(new SimpleActivityExportProcessor(exporter)));
    }

    private sealed class CaptureExporter : BaseExporter<Activity>
    {
        public ConcurrentBag<Activity> Activities { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Activities.Add(activity);
            return ExportResult.Success;
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Endpoint { get; private set; }
        public string? Authorization { get; private set; }
        public string Payload { get; private set; } = "";
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.GetValues("Authorization").Single();
            Payload = Encoding.UTF8.GetString(request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
