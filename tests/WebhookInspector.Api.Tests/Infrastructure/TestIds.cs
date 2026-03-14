namespace WebhookInspector.Api.Tests.Infrastructure;

public static class TestIds
{
    private static long _nextGitHubId = 1_000_000;

    /// <summary>Unique fake GitHub ids, so tests never share a user by accident.</summary>
    public static long NextGitHubId() => Interlocked.Increment(ref _nextGitHubId);
}
