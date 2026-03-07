using Microsoft.EntityFrameworkCore;
using WebhookInspector.Api.Capture;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Tests.Infrastructure;

public sealed record SeededEndpoint(Guid UserId, Guid EndpointId, string Token)
{
    public string CaptureUrl(string pathAndQuery = "") => $"/h/{Token}{pathAndQuery}";
}

public static class Seed
{
    private static long _nextGitHubId = 1_000_000;

    public static async Task<SeededEndpoint> EndpointAsync(PostgresFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            GitHubId = Interlocked.Increment(ref _nextGitHubId),
            Login = "test-user",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var endpoint = new WebhookEndpoint
        {
            Id = Guid.CreateVersion7(),
            Owner = user,
            Name = "test",
            CaptureToken = CaptureToken.Generate(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(endpoint);
        await db.SaveChangesAsync();
        return new SeededEndpoint(user.Id, endpoint.Id, endpoint.CaptureToken);
    }

    /// <summary>Inserts placeholder requests directly, to bring an endpoint close to its quota quickly.</summary>
    public static async Task RequestsAsync(PostgresFixture fixture, Guid endpointId, int count, int bodySizeEach = 0, DateTimeOffset? receivedAt = null)
    {
        await using var db = fixture.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "CapturedRequest" ("Id", "EndpointId", "ReceivedAt", "Method", "Path", "RawQuery", "Headers", "Body", "BodySize")
            SELECT gen_random_uuid(), {endpointId}, {receivedAt ?? DateTimeOffset.UtcNow}, 'POST', '/', '', '[]'::jsonb, ''::bytea, {bodySizeEach}
            FROM generate_series(1, {count})
            """);
    }

    public static async Task<List<CapturedRequest>> StoredAsync(PostgresFixture fixture, Guid endpointId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.CapturedRequests.AsNoTracking()
            .Where(r => r.EndpointId == endpointId)
            .OrderBy(r => r.ReceivedAt)
            .ToListAsync();
    }
}
