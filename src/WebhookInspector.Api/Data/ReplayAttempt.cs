namespace WebhookInspector.Api.Data;

/// <summary>One replay of a captured request to a URL the user chose, with what came back.</summary>
public class ReplayAttempt
{
    public Guid Id { get; set; }

    public Guid CapturedRequestId { get; set; }

    public CapturedRequest? CapturedRequest { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public required string TargetUrl { get; set; }

    public int DurationMs { get; set; }

    /// <summary>The response status, or null when no response arrived (see <see cref="Error"/>).</summary>
    public int? StatusCode { get; set; }

    public List<HeaderEntry> ResponseHeaders { get; set; } = [];

    /// <summary>At most <c>Replay:MaxResponseBytes</c> of the response body.</summary>
    public byte[] ResponseBody { get; set; } = [];

    public bool ResponseTruncated { get; set; }

    public string? Error { get; set; }
}
