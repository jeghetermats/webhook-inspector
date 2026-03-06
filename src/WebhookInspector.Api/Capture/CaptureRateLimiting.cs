using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace WebhookInspector.Api.Capture;

public static class CaptureRateLimiting
{
    /// <summary>
    /// Two fixed-window limits on the capture route, chained so both must allow the request:
    /// one per capture token and one per source IP. Other routes are not limited here.
    /// </summary>
    public static IServiceCollection AddCaptureRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>()
            .Configure<IOptions<CaptureOptions>>((limiter, capture) =>
            {
                var perToken = capture.Value.PermitsPerTokenPerMinute;
                var perIp = capture.Value.PermitsPerIpPerMinute;

                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.OnRejected = (context, _) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    }

                    return ValueTask.CompletedTask;
                };

                limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                    PartitionedRateLimiter.Create<HttpContext, string>(context =>
                        CaptureToken.TryGetFromPath(context.Request.Path, out var token)
                            ? RateLimitPartition.GetFixedWindowLimiter("token:" + token, _ => Window(perToken))
                            : RateLimitPartition.GetNoLimiter(string.Empty)),
                    PartitionedRateLimiter.Create<HttpContext, string>(context =>
                        context.Request.Path.StartsWithSegments("/h")
                            ? RateLimitPartition.GetFixedWindowLimiter(
                                "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                                _ => Window(perIp))
                            : RateLimitPartition.GetNoLimiter(string.Empty)));
            });

        return services;
    }

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
