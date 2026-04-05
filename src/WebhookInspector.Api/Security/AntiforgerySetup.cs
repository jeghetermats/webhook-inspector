using Microsoft.AspNetCore.Antiforgery;

namespace WebhookInspector.Api.Security;

public static class AntiforgerySetup
{
    public const string HeaderName = "X-XSRF-TOKEN";

    /// <summary>Readable by the SPA's JavaScript, which copies it into <see cref="HeaderName"/>.</summary>
    public const string RequestTokenCookieName = "XSRF-TOKEN";

    /// <summary>
    /// In production the cookie token uses the __Host- prefix and is HTTPS-only. Local
    /// development and end-to-end tests run over http://localhost, where antiforgery refuses
    /// to issue tokens for an HTTPS-only cookie (it throws), and a __Host- cookie without
    /// Secure is rejected by browsers, so there the cookie follows the request scheme.
    /// </summary>
    public static IServiceCollection AddAppAntiforgery(this IServiceCollection services, IHostEnvironment environment)
    {
        var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        return services.AddAntiforgery(options =>
        {
            options.HeaderName = HeaderName;
            options.Cookie.Name = local ? "wi-af" : "__Host-wi-af";
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
    }

    /// <summary>
    /// Issues a fresh token pair: the HttpOnly cookie token set by IAntiforgery, and the
    /// request token in a cookie the SPA can read. Call again after signing in.
    /// </summary>
    public static IResult IssueTokens(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(RequestTokenCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
        return Results.NoContent();
    }
}

/// <summary>
/// Requires a valid antiforgery token on every state-changing request under /api and /auth
/// (PLAN.md section 7). Validation happens here rather than per endpoint, so a new endpoint
/// cannot forget it.
/// </summary>
public sealed class AntiforgeryValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var path = context.Request.Path;
        var needsCheck =
            (path.StartsWithSegments("/api") || path.StartsWithSegments("/auth")) &&
            !(HttpMethods.IsGet(context.Request.Method) ||
              HttpMethods.IsHead(context.Request.Method) ||
              HttpMethods.IsOptions(context.Request.Method));

        if (needsCheck)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(
                        "Missing or invalid antiforgery token. Fetch /api/antiforgery and send the token in the X-XSRF-TOKEN header.",
                        statusCode: StatusCodes.Status400BadRequest)
                    .ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
