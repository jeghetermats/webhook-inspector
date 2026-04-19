using Microsoft.AspNetCore.SignalR;
using WebhookInspector.Api.Api;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Live;

public sealed class RequestNotifier(IHubContext<RequestsHub> hub, ILogger<RequestNotifier> logger)
{
    /// <summary>
    /// Best effort: a lost notification never means lost data, because clients refetch the
    /// newest page on reconnect and every 30 seconds. Failures are logged without request data.
    /// </summary>
    public async Task NotifyAsync(CapturedRequest request)
    {
        try
        {
            var payload = new RequestReceivedEvent(
                request.EndpointId,
                request.Id,
                request.Method,
                request.Path,
                request.ReceivedAt,
                request.BodySize,
                new RequestCursor(request.ReceivedAt, request.Id).Encode());

            await hub.Clients.Group(RequestsHub.GroupFor(request.EndpointId)).SendAsync(RequestsHub.RequestReceived, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Live notification failed for endpoint {EndpointId}: {ExceptionType}", request.EndpointId, ex.GetType().FullName);
        }
    }
}
