namespace WebhookInspector.Api.Replay;

/// <summary>Limits for replaying a captured request to another URL (PLAN.md section 13, ADR 0013).</summary>
public sealed class ReplayOptions
{
    public const string SectionName = "Replay";

    /// <summary>Total time for one attempt: connecting, sending and reading the response.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>At most this much of the response body is read and stored.</summary>
    public int MaxResponseBytes { get; set; } = 64 * 1024;

    public int MaxTargetUrlLength { get; set; } = 2048;

    /// <summary>Replays per user per minute. Replay makes this server send requests, so it is kept low.</summary>
    public int PermitsPerUserPerMinute { get; set; } = 10;

    /// <summary>Replays running at the same time across all users.</summary>
    public int MaxConcurrentReplays { get; set; } = 8;

    /// <summary>Only the newest attempts per captured request are kept.</summary>
    public int MaxAttemptsPerRequest { get; set; } = 20;
}
