namespace WebhookInspector.Api.Data;

public class User
{
    public Guid Id { get; set; }

    /// <summary>Numeric GitHub user id. Stable even if the user changes their login.</summary>
    public long GitHubId { get; set; }

    public required string Login { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<WebhookEndpoint> Endpoints { get; set; } = [];
}
