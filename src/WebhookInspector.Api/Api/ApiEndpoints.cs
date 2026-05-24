using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Security;

namespace WebhookInspector.Api.Api;

public static class ApiEndpoints
{
    /// <summary>Everything under /api requires a signed-in user.</summary>
    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            AntiforgerySetup.IssueTokens(context, antiforgery));

        api.MapGet("/me", (ClaimsPrincipal user) => new MeDto(user.GetUserId(), user.GetLogin()));

        api.MapEndpointsApi();
        api.MapRequestsApi();
        api.MapReplayApi();

        return app;
    }
}
