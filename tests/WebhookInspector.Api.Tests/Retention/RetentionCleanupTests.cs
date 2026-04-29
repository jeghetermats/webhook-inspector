using System.Net;
using Microsoft.Extensions.DependencyInjection;
using WebhookInspector.Api.Retention;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Retention;

[Collection(PostgresCollection.Name)]
public class RetentionCleanupTests(PostgresFixture fixture)
{
    private static async Task<int> RunCleanupAsync(ApiFactory api)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RetentionCleanup>().RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Deletes_only_requests_older_than_the_cutoff()
    {
        var endpoint = await Seed.EndpointAsync(fixture);
        await Seed.RequestsAsync(fixture, endpoint.EndpointId, 3, receivedAt: DateTimeOffset.UtcNow.AddDays(-8));
        await Seed.RequestsAsync(fixture, endpoint.EndpointId, 2, receivedAt: DateTimeOffset.UtcNow.AddDays(-7).AddMinutes(5));
        await Seed.RequestsAsync(fixture, endpoint.EndpointId, 4, receivedAt: DateTimeOffset.UtcNow);

        await RunCleanupAsync(fixture.Api);

        var remaining = await Seed.StoredAsync(fixture, endpoint.EndpointId);
        Assert.Equal(6, remaining.Count);
        Assert.All(remaining, r => Assert.True(r.ReceivedAt > DateTimeOffset.UtcNow.AddDays(-7)));
    }

    [Fact]
    public async Task Deletes_in_batches_until_nothing_old_is_left()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?> { ["Retention:BatchSize"] = "100" });
        var a = await Seed.EndpointAsync(fixture);
        var b = await Seed.EndpointAsync(fixture);
        await Seed.RequestsAsync(fixture, a.EndpointId, 250, receivedAt: DateTimeOffset.UtcNow.AddDays(-30));
        await Seed.RequestsAsync(fixture, b.EndpointId, 120, receivedAt: DateTimeOffset.UtcNow.AddDays(-10));

        var deleted = await RunCleanupAsync(api);

        Assert.True(deleted >= 370, $"deleted {deleted}"); // other tests' old rows may be included
        Assert.Empty(await Seed.StoredAsync(fixture, a.EndpointId));
        Assert.Empty(await Seed.StoredAsync(fixture, b.EndpointId));
    }

    [Fact]
    public async Task Captures_user_deletes_and_cleanup_together_never_exceed_the_quota()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        await Seed.RequestsAsync(fixture, endpoint.Id, 900, receivedAt: DateTimeOffset.UtcNow.AddDays(-8)); // cleanup removes these
        await Seed.RequestsAsync(fixture, endpoint.Id, 100);
        var capture = fixture.Api.CreateClient();

        var captures = Enumerable.Range(0, 150)
            .Select(i => capture.PostAsync($"/h/{endpoint.CaptureToken}/{i}", new StringContent("x")))
            .ToList();
        var cleanup = RunCleanupAsync(fixture.Api);
        var userDeletes = Enumerable.Range(0, 3).Select(async _ =>
        {
            var page = await user.ListRequestsAsync(endpoint.Id, "?limit=5");
            foreach (var item in page.Items)
            {
                await user.DeleteAsync($"/api/requests/{item.Id}");
            }
        });
        var allCaptures = Task.WhenAll(captures);
        await Task.WhenAll(allCaptures, cleanup, Task.WhenAll(userDeletes));

        var responses = await allCaptures;
        var stored = (await Seed.StoredAsync(fixture, endpoint.Id)).Count;
        var listed = (await user.ListEndpointsAsync()).Single();

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.TooManyRequests }));
        Assert.True(stored <= 1000, $"{stored} requests stored, over the quota of 1000");
        Assert.Equal(stored, listed.RequestCount); // computed usage matches the rows
    }
}
