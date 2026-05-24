using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace WebhookInspector.Api.Replay;

/// <summary>
/// Replay makes this server call other servers, so it has its own limits: a few per user per
/// minute, so the service cannot be used to flood a third party, and a small number at once,
/// so slow targets cannot tie up the replica. Applied inside the route because the
/// framework's rate limiter runs before authentication and cannot see the user.
/// </summary>
public sealed class ReplayLimiter : IDisposable
{
    public ReplayLimiter(IOptions<ReplayOptions> options)
    {
        var settings = options.Value;
        PerUser = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
            RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.PermitsPerUserPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
        Concurrency = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = settings.MaxConcurrentReplays,
            QueueLimit = 0,
        });
    }

    public PartitionedRateLimiter<Guid> PerUser { get; }

    public ConcurrencyLimiter Concurrency { get; }

    public void Dispose()
    {
        PerUser.Dispose();
        Concurrency.Dispose();
    }
}
