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
            ReceivedAt = time.GetUtcNow(),
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
            // Fallback: decoded path plus the query string ASP.NET Core parsed.
            context.Request.Path.StartsWithSegments(prefix, out var remaining);
            rest = remaining.Value + context.Request.QueryString.Value;
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
                if (value is not null)
                {
                    entries.Add(new HeaderEntry(name, value));
                }
            }
        }

        return entries;
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
}
