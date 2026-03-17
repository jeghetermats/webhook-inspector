using Microsoft.AspNetCore.Routing;

namespace WebhookInspector.Api.Spa;

/// <summary>
/// Serves the built React app from wwwroot (copied in by the Dockerfile), so the SPA and
/// the API share one origin. Client-side routes fall back to index.html, but unknown
/// paths under the server's own prefixes return a real 404.
/// </summary>
public static class SpaHosting
{
    private static readonly string[] ServerPrefixes = ["/api", "/auth", "/h", "/hubs", "/health", "/signin-github"];

    public static IApplicationBuilder UseSpaStaticFiles(this IApplicationBuilder app) =>
        app.UseDefaultFiles().UseStaticFiles();

    public static IEndpointRouteBuilder MapSpaFallback(this IEndpointRouteBuilder app)
    {
        // GET only: other methods must keep their normal 405/415 answers instead of
        // being swallowed by the fallback.
        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            if (ServerPrefixes.Any(prefix => path.StartsWithSegments(prefix)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var index = context.RequestServices.GetRequiredService<IWebHostEnvironment>()
                .WebRootFileProvider.GetFileInfo("index.html");
            if (!index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(index);
        }).WithMetadata(new HttpMethodMetadata([HttpMethods.Get]));

        return app;
    }
}
