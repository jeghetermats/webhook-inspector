namespace WebhookInspector.Api.Data;

/// <summary>
/// A user-owned capture endpoint. Mapped to the "Endpoint" table; the class has a
/// different name to avoid clashing with ASP.NET Core's routing <c>Endpoint</c> type.
/// </summary>
public class WebhookEndpoint
{
    /// <summary>Internal id, used by the management API. Never part of the capture URL.</summary>
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public User? Owner { get; set; }

    public required string Name { get; set; }

    /// <summary>Secret part of the public capture URL. Can be rotated.</summary>
    public required string CaptureToken { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<CapturedRequest> Requests { get; set; } = [];
}
