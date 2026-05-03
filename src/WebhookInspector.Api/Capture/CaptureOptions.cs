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

    /// <summary>Most endpoints one user can own.</summary>
    public int MaxEndpointsPerUser { get; set; } = 10;

    /// <summary>
    /// Captures processed at the same time. Beyond this they wait in a short queue, and beyond
    /// that they get 503 at once, so an overloaded replica sheds load instead of piling up
    /// requests that each hold a database connection (found in the phase 6 load test).
    /// </summary>
    public int MaxConcurrentCaptures { get; set; } = 16;

    /// <summary>Captures that may wait for a slot before new ones get 503.</summary>
    public int CaptureQueueLimit { get; set; } = 50;

    /// <summary>Requests per minute allowed for one capture token.</summary>
    public int PermitsPerTokenPerMinute { get; set; } = 120;

    /// <summary>Requests per minute allowed from one source IP across all tokens.</summary>
    public int PermitsPerIpPerMinute { get; set; } = 300;
}
