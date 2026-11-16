using System.Diagnostics;
using OpenTelemetry;

namespace CoinTracker.Shared.Observability;

public sealed class HttpTraceRedactionProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        // Discord webhook credentials are part of the URL path, which query redaction does not cover.
        foreach (var key in new[] { "url.full", "http.url", "url.path", "http.target" })
        {
            if (activity.GetTagItem(key) is not string value) continue;
            const string marker = "/webhooks/";
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                activity.SetTag(key, value[..(index + marker.Length)] + "[redacted]");
        }
    }
}
