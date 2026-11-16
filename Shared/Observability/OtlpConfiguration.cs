namespace CoinTracker.Shared.Observability;

internal static class OtlpConfiguration
{
    public static Uri? GetBaseEndpoint(IConfiguration configuration)
    {
        var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                "OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute HTTP(S) base URL without credentials, query, or fragment.");

        var protocol = configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];
        if (!string.IsNullOrWhiteSpace(protocol) && protocol != "http/protobuf")
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_PROTOCOL must be http/protobuf.");

        var baseUrl = uri.AbsoluteUri.TrimEnd('/');
        foreach (var suffix in new[] { "/v1/logs", "/v1/metrics", "/v1/traces" })
            if (baseUrl.EndsWith(suffix, StringComparison.Ordinal))
            {
                baseUrl = baseUrl[..^suffix.Length];
                break;
            }
        return new Uri(baseUrl + "/");
    }
}
