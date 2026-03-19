using System.Diagnostics;
using OpenTelemetry;

namespace WebhookInspector.Api.Telemetry;

/// <summary>
/// Runs before any exporter. For spans of the public capture route it keeps only an
/// allowlist of tags, so the capture token, the path after it, the query string and any
/// header-derived value (such as the user agent) never leave the process.
/// An allowlist instead of a denylist means a new tag added by a future instrumentation
/// version is dropped by default.
/// </summary>
public sealed class CaptureRedactionProcessor : BaseProcessor<Activity>
{
    public const string RedactedPath = "/h/{token}";

    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "http.request.method",
        "http.response.status_code",
        "http.route",
        "url.scheme",
        "network.protocol.name",
        "network.protocol.version",
        "server.address",
        "server.port",
        "error.type",
    };

    public override void OnEnd(Activity activity)
    {
        if (!IsCaptureSpan(activity))
        {
            return;
        }

        var toRemove = activity.TagObjects
            .Select(tag => tag.Key)
            .Where(key => !AllowedTags.Contains(key))
            .ToList();
        foreach (var key in toRemove)
        {
            activity.SetTag(key, null);
        }

        activity.SetTag("url.path", RedactedPath);
        var method = activity.GetTagItem("http.request.method") as string ?? string.Empty;
        activity.DisplayName = $"{method} {RedactedPath}".Trim();
    }

    private static bool IsCaptureSpan(Activity activity)
    {
        if (activity.Kind != ActivityKind.Server)
        {
            return false;
        }

        var path = activity.GetTagItem("url.path") as string;
        return path is not null && (path == "/h" || path.StartsWith("/h/", StringComparison.Ordinal));
    }
}
