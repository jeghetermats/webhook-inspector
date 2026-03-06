using System.Net;

namespace WebhookInspector.Api.Data;

/// <summary>One HTTP request received on a capture URL. Rows are insert-only, never updated.</summary>
public class CapturedRequest
{
    public Guid Id { get; set; }

    public Guid EndpointId { get; set; }

    public WebhookEndpoint? Endpoint { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public required string Method { get; set; }

    /// <summary>The part of the path after the capture token, starting with "/".</summary>
    public required string Path { get; set; }

    /// <summary>The query string exactly as received, without the leading "?".</summary>
    public required string RawQuery { get; set; }

    public List<HeaderEntry> Headers { get; set; } = [];

    public string? ContentType { get; set; }

    public byte[] Body { get; set; } = [];

    public int BodySize { get; set; }

    public IPAddress? SourceIp { get; set; }
}

/// <summary>One header value. A header sent several times becomes several entries with the same name.</summary>
public sealed record HeaderEntry(string Name, string Value);
