namespace WebhookInspector.Api.Capture;

/// <summary>Limits for the public capture route. Bound from the "Capture" configuration section.</summary>
public sealed class CaptureOptions
{
    public const string SectionName = "Capture";

    /// <summary>Largest accepted request body. Larger bodies get 413.</summary>
    public int MaxBodyBytes { get; set; } = 256 * 1024;

    /// <summary>Most requests stored per endpoint at any time.</summary>
    public int MaxRequestsPerEndpoint { get; set; } = 1_000;

    /// <summary>Most body bytes stored per endpoint at any time (headers not counted).</summary>
    public long MaxBodyBytesPerEndpoint { get; set; } = 50L * 1024 * 1024;

    /// <summary>Requests per minute allowed for one capture token.</summary>
    public int PermitsPerTokenPerMinute { get; set; } = 120;

    /// <summary>Requests per minute allowed from one source IP across all tokens.</summary>
    public int PermitsPerIpPerMinute { get; set; } = 300;
}
