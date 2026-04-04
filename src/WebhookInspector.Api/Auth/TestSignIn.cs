using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace WebhookInspector.Api.Auth;

/// <summary>
/// Sign-in without GitHub, for integration and end-to-end tests only (PLAN.md section 12).
/// POST /test/sign-in?githubId=..&amp;login=.. runs the same user upsert and issues the same
/// auth cookie as the GitHub callback.
///
/// Two locks: the environment must be "Testing" and TestSignIn:Enabled must be true.
/// Startup fails if the flag is set in any other environment, so a misconfiguration
/// cannot silently open the door.
/// </summary>
public static class TestSignIn
{
    public const string ConfigKey = "TestSignIn:Enabled";

    public static IEndpointRouteBuilder MapTestSignIn(this WebApplication app)
    {
        var requested = app.Configuration.GetValue<bool>(ConfigKey);
        if (!requested)
        {
            return app;
        }

        if (!app.Environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is set in the '{app.Environment.EnvironmentName}' environment. Test sign-in is only allowed in 'Testing'.");
        }

        app.MapPost("/test/sign-in", async (HttpContext context, UserService users, long githubId, string login) =>
        {
            var userId = await users.UpsertFromGitHubAsync(githubId, login, context.RequestAborted);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                AppPrincipal.Create(userId, githubId, login));
            return Results.Ok(new { userId });
        }).ExcludeFromDescription();

        return app;
    }
}
