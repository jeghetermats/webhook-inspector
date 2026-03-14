using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Capture;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Api;

/// <summary>
/// Endpoint management. Every query filters on the signed-in user's id, and another
/// user's endpoint answers 404 exactly like a missing one, so ids cannot be probed.
/// </summary>
public static class EndpointsApi
{
    public const int MaxNameLength = 100;

    public static RouteGroupBuilder MapEndpointsApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/endpoints");

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPatch("/{id:guid}", RenameAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapPost("/{id:guid}/rotate-token", RotateTokenAsync);
        group.MapDelete("/{id:guid}/requests", ClearRequestsAsync);

        return group;
    }

    private static IQueryable<EndpointDto> OwnedBy(this IQueryable<WebhookEndpoint> endpoints, Guid userId) =>
        endpoints
            .Where(e => e.OwnerId == userId)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new EndpointDto(
                e.Id,
                e.Name,
                e.CaptureToken,
                e.CreatedAt,
                e.Requests.Count(),
                e.Requests.Sum(r => (long)r.BodySize)));

    private static async Task<List<EndpointDto>> ListAsync(ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        await db.Endpoints.OwnedBy(user.GetUserId()).ToListAsync(ct);

    private static async Task<IResult> CreateAsync(
        EndpointNameRequest body,
        ClaimsPrincipal user,
        AppDbContext db,
        IOptions<CaptureOptions> options,
        TimeProvider time,
        CancellationToken ct)
    {
        if (ValidateName(body.Name) is { } invalid)
        {
            return invalid;
        }

        var userId = user.GetUserId();
        var max = options.Value.MaxEndpointsPerUser;

        // Same pattern as the capture quota: lock the owning row, then count in a separate
        // statement, so parallel creates cannot exceed the limit.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "User" WHERE "Id" = {userId} FOR UPDATE""", ct);
        if (await db.Endpoints.CountAsync(e => e.OwnerId == userId, ct) >= max)
        {
            return Results.Problem($"You can have at most {max} endpoints. Delete one to create another.", statusCode: StatusCodes.Status409Conflict);
        }

        var endpoint = new WebhookEndpoint
        {
            Id = Guid.CreateVersion7(),
            OwnerId = userId,
            Name = body.Name!.Trim(),
            CaptureToken = CaptureToken.Generate(),
            CreatedAt = time.GetUtcNowForDb(),
        };
        db.Endpoints.Add(endpoint);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var dto = new EndpointDto(endpoint.Id, endpoint.Name, endpoint.CaptureToken, endpoint.CreatedAt, 0, 0);
        return Results.Created($"/api/endpoints/{endpoint.Id}", dto);
    }

    private static async Task<IResult> RenameAsync(
        Guid id, EndpointNameRequest body, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        if (ValidateName(body.Name) is { } invalid)
        {
            return invalid;
        }

        var userId = user.GetUserId();
        var updated = await db.Endpoints
            .Where(e => e.Id == id && e.OwnerId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, body.Name!.Trim()), ct);

        return updated == 0
            ? Results.NotFound()
            : Results.Ok(await db.Endpoints.Where(e => e.Id == id).OwnedBy(userId).SingleAsync(ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        // Captured requests go with it through the foreign key's ON DELETE CASCADE.
        var deleted = await db.Endpoints.Where(e => e.Id == id && e.OwnerId == userId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> RotateTokenAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var newToken = CaptureToken.Generate();
        var updated = await db.Endpoints
            .Where(e => e.Id == id && e.OwnerId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CaptureToken, newToken), ct);

        return updated == 0
            ? Results.NotFound()
            : Results.Ok(await db.Endpoints.Where(e => e.Id == id).OwnedBy(userId).SingleAsync(ct));
    }

    private static async Task<IResult> ClearRequestsAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var userId = user.GetUserId();
        if (!await db.Endpoints.AnyAsync(e => e.Id == id && e.OwnerId == userId, ct))
        {
            return Results.NotFound();
        }

        // Only removes CapturedRequest rows and never locks the endpoint row (ADR 0005).
        await db.CapturedRequests.Where(r => r.EndpointId == id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static IResult? ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"Name is required and can be at most {MaxNameLength} characters."],
            });
        }

        return null;
    }
}
