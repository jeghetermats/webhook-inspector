using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Live;

/// <summary>
/// Live notifications for new captured requests (PLAN.md section 8). The database is the
/// source of truth; this hub only says "something new arrived" (ADR 0003).
///
/// Groups are per connection and are not a security boundary on their own, so every
/// Subscribe checks that the endpoint belongs to the signed-in user. A reconnect is a new
/// connection, so the client subscribes again and is checked again.
/// </summary>
[Authorize]
public sealed class RequestsHub(AppDbContext db) : Hub
{
    public const string Path = "/hubs/requests";
    public const string RequestReceived = "RequestReceived";

    public static string GroupFor(Guid endpointId) => $"endpoint:{endpointId:N}";

    public async Task Subscribe(Guid endpointId)
    {
        var userId = Context.User!.GetUserId();
        var owned = await db.Endpoints.AnyAsync(e => e.Id == endpointId && e.OwnerId == userId, Context.ConnectionAborted);
        if (!owned)
        {
            // Same answer for "not yours" and "does not exist", like the HTTP API's 404.
            throw new HubException("Endpoint not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(endpointId), Context.ConnectionAborted);
    }

    public Task Unsubscribe(Guid endpointId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(endpointId), Context.ConnectionAborted);
}

/// <summary>
/// Sent after the capture transaction has committed. Metadata only: no body, header values
/// or query string, so nothing sensitive travels over the live channel.
/// </summary>
public sealed record RequestReceivedEvent(
    Guid EndpointId,
    Guid RequestId,
    string Method,
    string Path,
    DateTimeOffset ReceivedAt,
    int BodySize,
    string Cursor);
