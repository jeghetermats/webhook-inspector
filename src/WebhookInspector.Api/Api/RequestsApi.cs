using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Capture;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Api;

/// <summary>Captured request history. Ownership is checked through the request's endpoint.</summary>
public static class RequestsApi
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static RouteGroupBuilder MapRequestsApi(this RouteGroupBuilder api)
    {
        api.MapGet("/endpoints/{endpointId:guid}/requests", ListAsync);

        var group = api.MapGroup("/requests");
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/body", DownloadBodyAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return api;
    }

    private static IQueryable<CapturedRequest> OwnedBy(this IQueryable<CapturedRequest> requests, Guid userId) =>
        requests.Where(r => r.Endpoint!.OwnerId == userId);

    private static async Task<IResult> ListAsync(
        Guid endpointId,
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken ct,
        string? cursor = null,
        int? limit = null,
        string? method = null,
        string? pathPrefix = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var userId = user.GetUserId();
        if (!await db.Endpoints.AnyAsync(e => e.Id == endpointId && e.OwnerId == userId, ct))
        {
            return Results.NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["limit"] = [$"Limit must be between 1 and {MaxPageSize}."];
        }

        RequestCursor after = default;
        if (cursor is not null && !RequestCursor.TryDecode(cursor, out after))
        {
            errors["cursor"] = ["Invalid cursor."];
        }

        if (method is not null && !CaptureEndpoints.Methods.Contains(method.ToUpperInvariant()))
        {
            errors["method"] = ["Unknown method."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var query = db.CapturedRequests.Where(r => r.EndpointId == endpointId);

        if (cursor is not null)
        {
            // Row-value comparison: (ReceivedAt, Id) < (cursor.ReceivedAt, cursor.Id),
            // which matches the descending index exactly.
            query = query.Where(r => EF.Functions.LessThan(
                ValueTuple.Create(r.ReceivedAt, r.Id),
                ValueTuple.Create(after.ReceivedAt, after.Id)));
        }

        if (method is not null)
        {
            var upper = method.ToUpperInvariant();
            query = query.Where(r => r.Method == upper);
        }

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            query = query.Where(r => r.Path.StartsWith(pathPrefix));
        }

        if (from is not null)
        {
            query = query.Where(r => r.ReceivedAt >= from.Value);
        }

        if (to is not null)
        {
            query = query.Where(r => r.ReceivedAt <= to.Value);
        }

        var rows = await query
            .OrderByDescending(r => r.ReceivedAt)
            .ThenByDescending(r => r.Id)
            .Take(pageSize + 1)
            .Select(r => new
            {
                r.Id,
                r.Method,
                r.Path,
                r.RawQuery,
                r.ReceivedAt,
                r.ContentType,
                r.BodySize,
                Prefix = AppDbContext.BytesPrefix(r.Body, 1, BodyPreview.PrefixBytes),
            })
            .ToListAsync(ct);

        var items = rows.Take(pageSize)
            .Select(r => new RequestSummaryDto(
                r.Id, r.Method, r.Path, r.RawQuery, r.ReceivedAt, r.ContentType, r.BodySize, BodyPreview.FromPrefix(r.Prefix)))
            .ToList();

        var nextCursor = rows.Count > pageSize
            ? new RequestCursor(items[^1].ReceivedAt, items[^1].Id).Encode()
            : null;

        return Results.Ok(new RequestPageDto(items, nextCursor));
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var request = await db.CapturedRequests.AsNoTracking()
            .OwnedBy(user.GetUserId())
            .SingleOrDefaultAsync(r => r.Id == id, ct);

        return request is null
            ? Results.NotFound()
            : Results.Ok(new RequestDetailDto(
                request.Id,
                request.EndpointId,
                request.Method,
                request.Path,
                request.RawQuery,
                request.ReceivedAt,
                request.Headers,
                request.ContentType,
                request.BodySize,
                Convert.ToBase64String(request.Body),
                request.SourceIp?.ToString()));
    }

    /// <summary>
    /// Raw body as a download. Always application/octet-stream with Content-Disposition:
    /// attachment (and nosniff from the global headers), so captured content is never
    /// rendered by the browser on this origin.
    /// </summary>
    private static async Task<IResult> DownloadBodyAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var bodies = await db.CapturedRequests
            .OwnedBy(user.GetUserId())
            .Where(r => r.Id == id)
            .Select(r => r.Body)
            .ToListAsync(ct);

        return bodies.Count == 0
            ? Results.NotFound()
            : Results.File(bodies[0], "application/octet-stream", $"request-{id}.bin");
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var deleted = await db.CapturedRequests
            .OwnedBy(user.GetUserId())
            .Where(r => r.Id == id)
            .ExecuteDeleteAsync(ct);

        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }
}
