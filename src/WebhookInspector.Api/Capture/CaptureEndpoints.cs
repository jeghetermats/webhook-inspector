using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Capture;

public static class CaptureEndpoints
{
    public static readonly string[] Methods =
        [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete, HttpMethods.Head, HttpMethods.Options];

    public static IEndpointRouteBuilder MapCapture(this IEndpointRouteBuilder app)
    {
        // {**path} also matches nothing, so /h/{token} works too.
        app.MapMethods("/h/{token}/{**path}", Methods, HandleAsync)
            .WithName("Capture")
            .RequireRateLimiting(CaptureRateLimiting.ConcurrencyPolicy)
            .ExcludeFromDescription();

        return app;
    }

    // Logging policy for this route (PLAN.md section 7): log endpoint id, method,
    // body size and status only. Never the token, path, query, headers or body,
    // and never an exception message, which could contain request data.
    private static async Task<IResult> HandleAsync(
        HttpContext context,
        string token,
        CaptureStore store,
        Live.RequestNotifier notifier,
        IOptions<CaptureOptions> options,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("WebhookInspector.Capture");
        var request = context.Request;
        var method = request.Method;
        var cancellationToken = context.RequestAborted;

        if (!CaptureToken.IsWellFormed(token))
        {
            logger.LogInformation("Capture rejected: {Method}, status {StatusCode}", method, StatusCodes.Status404NotFound);
            return Results.NotFound();
        }

        var body = await BodyReader.ReadLimitedAsync(request, options.Value.MaxBodyBytes, cancellationToken);
        if (body is null)
        {
            logger.LogInformation("Capture rejected: {Method}, status {StatusCode}", method, StatusCodes.Status413PayloadTooLarge);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var (path, rawQuery) = SplitTarget(context, token);
        var captured = new CapturedRequest
        {
            Id = Guid.CreateVersion7(),
            ReceivedAt = time.GetUtcNowForDb(),
            Method = method,
            Path = path,
            RawQuery = rawQuery,
            Headers = ReadHeaders(request.Headers),
            ContentType = request.ContentType,
            Body = body,
            BodySize = body.Length,
            SourceIp = Normalize(context.Connection.RemoteIpAddress),
        };

        CaptureResult result;
        try
        {
            result = await store.StoreAsync(token, captured, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // client went away; nothing to report
        }
        catch (Exception ex)
        {
            logger.LogError("Capture failed: {Method}, {BodySize} bytes, {ExceptionType}", method, body.Length, ex.GetType().FullName);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        switch (result.Outcome)
        {
            case CaptureOutcome.UnknownToken:
                logger.LogInformation("Capture rejected: {Method}, status {StatusCode}", method, StatusCodes.Status404NotFound);
                return Results.NotFound();

            case CaptureOutcome.QuotaExceeded:
                logger.LogInformation(
                    "Capture rejected on endpoint {EndpointId}: {Method}, {BodySize} bytes, status {StatusCode}",
                    result.EndpointId, method, body.Length, StatusCodes.Status429TooManyRequests);
                return Results.Text(
                    "Quota full: this endpoint already stores the maximum number of requests or body bytes. Delete old requests to make room.",
                    statusCode: StatusCodes.Status429TooManyRequests);

            default:
                logger.LogInformation(
                    "Captured request on endpoint {EndpointId}: {Method}, {BodySize} bytes, status {StatusCode}",
                    result.EndpointId, method, body.Length, StatusCodes.Status200OK);
                // The transaction has committed, so anyone told about this request can load it.
                await notifier.NotifyAsync(captured);
                return HttpMethods.IsHead(method) ? Results.Ok() : Results.Json(new { ok = true });
        }
    }

    /// <summary>
    /// Returns the path after the token and the raw query, taken from the request target
    /// exactly as received (percent-encoding preserved) when available.
    /// </summary>
    internal static (string Path, string RawQuery) SplitTarget(HttpContext context, string token)
    {
        var prefix = "/h/" + token;
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;

        string rest;
        if (rawTarget is not null && rawTarget.StartsWith(prefix, StringComparison.Ordinal))
        {
            rest = rawTarget[prefix.Length..];
        }
        else
        {
            // Fallback when the server does not expose the raw target: re-encode the
            // decoded path, and use the query string, which ASP.NET Core keeps encoded.
            context.Request.Path.StartsWithSegments(prefix, out var remaining);
            rest = remaining.ToUriComponent() + context.Request.QueryString.Value;
        }

        var queryStart = rest.IndexOf('?');
        var path = queryStart < 0 ? rest : rest[..queryStart];
        var query = queryStart < 0 ? string.Empty : rest[(queryStart + 1)..];
        return (path.Length == 0 ? "/" : path, query);
    }

    private static List<HeaderEntry> ReadHeaders(IHeaderDictionary headers)
    {
        var entries = new List<HeaderEntry>(headers.Count);
        foreach (var (name, values) in headers)
        {
            foreach (var value in values)
            {
                var stored = value is not null && name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                    ? WithoutOwnCookies(value)
                    : value;
                if (stored is not null)
                {
                    entries.Add(new HeaderEntry(name, stored));
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Removes this app's own cookies from a captured Cookie header. A signed-in user who
    /// opens their capture URL in the same browser sends their session cookie with it
    /// (SameSite=Lax allows top-level GETs), and storing it would put a working session
    /// token into the request history. Other cookies are kept: they are what was sent.
    /// Returns null when nothing is left.
    /// </summary>
    internal static string? WithoutOwnCookies(string cookieHeader)
    {
        var kept = cookieHeader
            .Split(';')
            .Select(pair => pair.Trim())
            .Where(pair => pair.Length > 0 && !IsOwnCookie(pair.Split('=', 2)[0].Trim()))
            .ToList();
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    private static bool IsOwnCookie(string name) =>
        name.StartsWith(Auth.AuthSetup.AuthCookieName, StringComparison.Ordinal) || // incl. chunked C1, C2, ...
        name == Security.AntiforgerySetup.CookieTokenName ||
        name == Security.AntiforgerySetup.LocalCookieTokenName ||
        name == Security.AntiforgerySetup.RequestTokenCookieName ||
        name.StartsWith(".AspNetCore.", StringComparison.Ordinal); // OAuth correlation and nonce cookies

    private static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
}
