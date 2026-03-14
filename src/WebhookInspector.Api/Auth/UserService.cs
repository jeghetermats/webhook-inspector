using Microsoft.EntityFrameworkCore;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Auth;

public sealed class UserService(AppDbContext db, TimeProvider time)
{
    /// <summary>
    /// Creates the user on first sign-in, or updates the login if it changed on GitHub.
    /// A single upsert statement, so two simultaneous first sign-ins cannot create duplicates.
    /// </summary>
    public async Task<Guid> UpsertFromGitHubAsync(long gitHubId, string login, CancellationToken cancellationToken)
    {
        var newId = Guid.CreateVersion7();
        var now = time.GetUtcNowForDb();
        var ids = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO "User" ("Id", "GitHubId", "Login", "CreatedAt")
            VALUES ({newId}, {gitHubId}, {login}, {now})
            ON CONFLICT ("GitHubId") DO UPDATE SET "Login" = EXCLUDED."Login"
            RETURNING "Id" AS "Value"
            """).ToListAsync(cancellationToken);
        return ids.Single();
    }
}
