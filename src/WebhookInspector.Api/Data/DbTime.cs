namespace WebhookInspector.Api.Data;

public static class DbTime
{
    /// <summary>
    /// Current UTC time at the precision Postgres stores (microseconds). Using this for every
    /// stored timestamp means the value returned to a client right after an insert is the same
    /// value it would read back later, which matters for cursors and de-duplication.
    /// </summary>
    public static DateTimeOffset GetUtcNowForDb(this TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % (TimeSpan.TicksPerMillisecond / 1000)));
    }
}
