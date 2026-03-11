namespace WebhookInspector.Api.Security;

public static class SecurityHeaders
{
    public const string ContentSecurityPolicy = "default-src 'self'; connect-src 'self'; frame-ancestors 'none'";

    /// <summary>Headers on every response. Captured content is untrusted and must never be sniffed or framed.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });
}
