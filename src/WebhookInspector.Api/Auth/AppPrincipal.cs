using System.Globalization;
using System.Security.Claims;

namespace WebhookInspector.Api.Auth;

/// <summary>The claims the app puts in its auth cookie, and how to read them back.</summary>
public static class AppPrincipal
{
    public const string GitHubIdClaim = "github_id";
    private const string AuthenticationType = "WebhookInspector";

    public static ClaimsPrincipal Create(Guid userId, long gitHubId, string login)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, login),
                new Claim(GitHubIdClaim, gitHubId.ToString(CultureInfo.InvariantCulture)),
            ],
            AuthenticationType);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>The app's own user id. Only valid on authenticated principals created by <see cref="Create"/>.</summary>
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Principal has no user id claim."));

    public static string GetLogin(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
}
