using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Data;
using WebhookInspector.Api.Replay;

namespace WebhookInspector.Api.Api;

public sealed record ReplayCreateRequest(string? TargetUrl);

public sealed record ReplayAttemptDto(
    Guid Id,
    Guid RequestId,
    string TargetUrl,
    DateTimeOffset StartedAt,
    int DurationMs,
    int? StatusCode,
    IReadOnlyList<HeaderEntry> ResponseHeaders,
    string ResponseBodyBase64,
    bool ResponseTruncated,
    string? Error);

/// <summary>
/// Replay a captured request to a URL the user chooses (PLAN.md section 13, ADR 0013).
/// Ownership is checked through the request's endpoint, like every other request route.
/// </summary>
public static class ReplayApi
{
    public static RouteGroupBuilder MapReplayApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/requests/{id:guid}/replays");
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        return api;
    }

    private static async Task<IResult> ListAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (!await db.CapturedRequests.AnyAsync(r => r.Id == id && r.Endpoint!.OwnerId == userId, ct))
        {
            return Results.NotFound();
        }

        var attempts = await db.ReplayAttempts.AsNoTracking()
            .Where(a => a.CapturedRequestId == id)
            .OrderByDescending(a => a.StartedAt)
            .ThenByDescending(a => a.Id)
            .ToListAsync(ct);

        return Results.Ok(attempts.Select(ToDto));
    }

    private static async Task<IResult> CreateAsync(
        Guid id,
        ReplayCreateRequest body,
        ClaimsPrincipal user,
        AppDbContext db,
        ReplayClient client,
        ReplayLimiter limiter,
        TimeProvider time,
        IOptions<ReplayOptions> options,
        ILogger<ReplayClient> logger,
        CancellationToken ct)
    {
        if (!TryParseTarget(body.TargetUrl, options.Value, out var target, out var problem))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["targetUrl"] = [problem] });
        }

        var userId = user.GetUserId();
        var captured = await db.CapturedRequests.AsNoTracking()
            .Where(r => r.Id == id && r.Endpoint!.OwnerId == userId)
            .Select(r => new { r.Method, r.Headers, r.Body })
            .SingleOrDefaultAsync(ct);
        if (captured is null)
        {
            return Results.NotFound();
        }

        using var userLease = limiter.PerUser.AttemptAcquire(userId);
        if (!userLease.IsAcquired)
        {
            return TooMany(userLease, StatusCodes.Status429TooManyRequests, "Too many replays. Wait a minute and try again.");
        }

        using var slot = limiter.Concurrency.AttemptAcquire();
        if (!slot.IsAcquired)
        {
            return TooMany(slot, StatusCodes.Status503ServiceUnavailable, "Too many replays are running. Try again in a few seconds.");
        }

        var attemptId = Guid.CreateVersion7();
        var startedAt = time.GetUtcNowForDb();
        // The database connection is not held while the target is being called.
        var result = await client.SendAsync(
            new ReplayRequest(target, captured.Method, captured.Headers, captured.Body, attemptId), ct);

        var attempt = new ReplayAttempt
        {
            Id = attemptId,
            CapturedRequestId = id,
            StartedAt = startedAt,
            TargetUrl = target.AbsoluteUri,
            DurationMs = result.DurationMs,
            StatusCode = result.StatusCode,
            ResponseHeaders = result.ResponseHeaders.ToList(),
            ResponseBody = result.ResponseBody,
            ResponseTruncated = result.ResponseTruncated,
            Error = result.Error,
        };
        db.ReplayAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);

        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM "ReplayAttempt"
            WHERE "CapturedRequestId" = {id}
              AND "Id" NOT IN (
                SELECT "Id" FROM "ReplayAttempt"
                WHERE "CapturedRequestId" = {id}
                ORDER BY "StartedAt" DESC, "Id" DESC
                LIMIT {options.Value.MaxAttemptsPerRequest})
            """, ct);

        // No URL, headers or body in the log: the target may carry secrets.
        logger.LogInformation(
            "Replay {AttemptId} of request {RequestId}: outcome {Outcome}, status {StatusCode}, {DurationMs} ms",
            attemptId, id, result.Outcome, result.StatusCode, result.DurationMs);

        return Results.Created($"/api/requests/{id}/replays", ToDto(attempt));
    }

    /// <summary>
    /// Shape checks only. Whether the target may be reached is decided when connecting,
    /// against the address actually used, so a host name is never trusted here.
    /// </summary>
    internal static bool TryParseTarget(string? value, ReplayOptions options, out Uri target, out string problem)
    {
        target = null!;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "Enter a URL.";
            return false;
        }

        if (value.Length > options.MaxTargetUrlLength)
        {
            problem = $"The URL can be at most {options.MaxTargetUrlLength} characters.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
        {
            problem = "Enter an absolute http:// or https:// URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            problem = "Credentials in the URL are not supported.";
            return false;
        }

        target = new UriBuilder(uri) { Fragment = string.Empty }.Uri;
        return true;
    }

    private static IResult TooMany(RateLimitLease lease, int status, string detail)
    {
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(2);
        return new RetryAfterResult(
            Results.Problem(detail: detail, statusCode: status),
            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
    }

    private static ReplayAttemptDto ToDto(ReplayAttempt a) => new(
        a.Id,
        a.CapturedRequestId,
        a.TargetUrl,
        a.StartedAt,
        a.DurationMs,
        a.StatusCode,
        a.ResponseHeaders,
        Convert.ToBase64String(a.ResponseBody),
        a.ResponseTruncated,
        a.Error);

    private sealed class RetryAfterResult(IResult inner, string seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.Headers.RetryAfter = seconds;
            return inner.ExecuteAsync(context);
        }
    }
}
