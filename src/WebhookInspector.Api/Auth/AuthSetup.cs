using System.Globalization;
using System.Security.Claims;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace WebhookInspector.Api.Auth;

public static class AuthSetup
{
    public const string AuthCookieName = "__Host-wi-auth";

    public static IServiceCollection AddAppAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<UserService>();

        var authentication = services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                // __Host- prefix: browser only accepts it with Secure, Path=/ and no Domain.
                options.Cookie.Name = AuthCookieName;
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;

                // The SPA calls the API with fetch, so answer with status codes instead of redirects.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        var clientId = configuration["Authentication:GitHub:ClientId"];
        if (!string.IsNullOrEmpty(clientId))
        {
            authentication.AddGitHub(options =>
            {
                options.ClientId = clientId;
                options.ClientSecret = configuration["Authentication:GitHub:ClientSecret"]
                    ?? throw new InvalidOperationException("Authentication:GitHub:ClientSecret is missing.");
                options.CallbackPath = "/signin-github";
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

                // A failed sign-in (user cancelled, misconfigured OAuth app, expired state) would
                // otherwise surface as an unhandled 500. The handler has already logged the cause.
                options.Events.OnRemoteFailure = context =>
                {
                    context.Response.Redirect("/?signin=failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };

                // Replace GitHub's claims with the app's own: the cookie carries our user id,
                // and the GitHub id only for reference. No GitHub access token is stored.
                options.SaveTokens = false;
                options.Events.OnCreatingTicket = async context =>
                {
                    var gitHubId = long.Parse(
                        context.Identity!.FindFirst(ClaimTypes.NameIdentifier)!.Value, CultureInfo.InvariantCulture);
                    var login = context.Identity.FindFirst(ClaimTypes.Name)?.Value ?? "unknown";

                    var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
                    var userId = await users.UpsertFromGitHubAsync(gitHubId, login, context.HttpContext.RequestAborted);
                    context.Principal = AppPrincipal.Create(userId, gitHubId, login);
                };
            });
        }

        services.AddAuthorization();
        return services;
    }

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").ExcludeFromDescription();

        group.MapGet("/login", async (HttpContext context, IAuthenticationSchemeProvider schemes, string? returnUrl) =>
        {
            if (await schemes.GetSchemeAsync(GitHubAuthenticationDefaults.AuthenticationScheme) is null)
            {
                return Results.Problem("GitHub sign-in is not configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var properties = new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) };
            return Results.Challenge(properties, [GitHubAuthenticationDefaults.AuthenticationScheme]);
        });

        // Protected by the antiforgery middleware like every other state-changing request.
        group.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>Only same-site relative paths, so /auth/login cannot be used as an open redirect.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } &&
        returnUrl[0] == '/' &&
        !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/";
}
