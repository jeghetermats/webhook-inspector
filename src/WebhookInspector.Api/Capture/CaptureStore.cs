using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Capture;

public enum CaptureOutcome
{
    Stored,
    UnknownToken,
    QuotaExceeded,
}

public readonly record struct CaptureResult(CaptureOutcome Outcome, Guid? EndpointId);

/// <summary>
/// Stores a captured request while enforcing the per-endpoint quota.
/// See docs/PLAN.md section 7 and ADR 0005 for why the protocol looks like this.
/// </summary>
public sealed class CaptureStore(AppDbContext db, IOptions<CaptureOptions> options)
{
    private readonly CaptureOptions _options = options.Value;

    public async Task<CaptureResult> StoreAsync(string token, CapturedRequest request, CancellationToken cancellationToken)
    {
        // READ COMMITTED: every statement takes a fresh snapshot. This is what makes the
        // separate count below see rows committed by the transaction we waited for.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Lock the endpoint row. Concurrent captures for the same endpoint queue up here.
        var locked = await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Endpoint" WHERE "CaptureToken" = {token} FOR UPDATE""")
            .ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CaptureResult(CaptureOutcome.UnknownToken, null);
        }

        var endpointId = locked[0];

        // 2. Count in a SEPARATE statement, after the lock is granted. Do not merge this
        //    into the locking query or a CTE: that would count from the snapshot taken
        //    before waiting for the lock and could miss the previous holder's insert.
        var usage = (await db.Database
            .SqlQuery<EndpointUsage>($"""
                SELECT count(*)::int AS "Count", coalesce(sum("BodySize"), 0)::bigint AS "BodyBytes"
                FROM "CapturedRequest"
                WHERE "EndpointId" = {endpointId}
                """)
            .ToListAsync(cancellationToken)).Single();

        // 3. Insert only if the new request fits both limits.
        if (usage.Count + 1 > _options.MaxRequestsPerEndpoint ||
            usage.BodyBytes + request.BodySize > _options.MaxBodyBytesPerEndpoint)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CaptureResult(CaptureOutcome.QuotaExceeded, endpointId);
        }

        request.EndpointId = endpointId;
        db.CapturedRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);

        // 4. Commit releases the row lock.
        await transaction.CommitAsync(cancellationToken);
        return new CaptureResult(CaptureOutcome.Stored, endpointId);
    }
}

public sealed class EndpointUsage
{
    public int Count { get; init; }

    public long BodyBytes { get; init; }
}
