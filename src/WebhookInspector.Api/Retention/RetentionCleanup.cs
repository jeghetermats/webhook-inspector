using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Retention;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Requests older than this are deleted at the next cleanup run.</summary>
    public int Days { get; set; } = 7;

    /// <summary>Rows per DELETE statement, so one run never holds a huge transaction.</summary>
    public int BatchSize { get; set; } = 1000;
}

/// <summary>
/// Deletes captured requests older than the retention period (PLAN.md section 9). Runs as a
/// scheduled Container Apps Job, independent of the app, so it also runs while the app has
/// scaled to zero (ADR 0009).
///
/// Like user deletes, it only removes CapturedRequest rows and never locks the endpoint row,
/// so it cannot deadlock with captures and can only free quota (ADR 0005).
/// </summary>
public sealed class RetentionCleanup(
    AppDbContext db,
    TimeProvider time,
    IOptions<RetentionOptions> options,
    ILogger<RetentionCleanup> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var cutoff = time.GetUtcNow().AddDays(-settings.Days);
        var batchSize = settings.BatchSize;
        var total = 0;

        while (true)
        {
            // Uses the ReceivedAt index. Each batch is its own short statement.
            var deleted = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM "CapturedRequest"
                WHERE "Id" IN (
                    SELECT "Id" FROM "CapturedRequest"
                    WHERE "ReceivedAt" < {cutoff}
                    ORDER BY "ReceivedAt"
                    LIMIT {batchSize})
                """, cancellationToken);

            total += deleted;
            if (deleted < batchSize)
            {
                break;
            }
        }

        logger.LogInformation(
            "Retention cleanup deleted {DeletedCount} requests received before {Cutoff:O}", total, cutoff);
        return total;
    }
}
