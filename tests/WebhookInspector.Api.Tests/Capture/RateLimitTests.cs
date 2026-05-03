using Microsoft.EntityFrameworkCore;
using System.Net;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Capture;

[Collection(PostgresCollection.Name)]
public class RateLimitTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Per_token_limit_returns_429_with_retry_after()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?>
        {
            ["Capture:PermitsPerTokenPerMinute"] = "3",
        });
        var client = api.CreateClient();
        var endpoint = await Seed.EndpointAsync(fixture);
        var other = await Seed.EndpointAsync(fixture);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await client.PostAsync(endpoint.CaptureUrl(), new StringContent("x"))).StatusCode);
        }

        var limited = await client.PostAsync(endpoint.CaptureUrl(), new StringContent("x"));
        var otherToken = await client.PostAsync(other.CaptureUrl(), new StringContent("x"));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, otherToken.StatusCode); // other tokens are unaffected
        Assert.Equal(3, (await Seed.StoredAsync(fixture, endpoint.EndpointId)).Count);
    }

    [Fact]
    public async Task Per_ip_limit_applies_across_tokens()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?>
        {
            ["Capture:PermitsPerIpPerMinute"] = "2",
        });
        var client = api.CreateClient();
        var a = await Seed.EndpointAsync(fixture);
        var b = await Seed.EndpointAsync(fixture);
        var c = await Seed.EndpointAsync(fixture);

        var first = await client.PostAsync(a.CaptureUrl(), new StringContent("x"));
        var second = await client.PostAsync(b.CaptureUrl(), new StringContent("x"));
        var third = await client.PostAsync(c.CaptureUrl(), new StringContent("x"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Other_routes_are_not_limited_by_the_capture_policy()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?>
        {
            ["Capture:PermitsPerIpPerMinute"] = "1",
        });
        var client = api.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        }
    }

    [Fact]
    public async Task Captures_over_the_concurrency_limit_get_503_with_retry_after()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?>
        {
            ["Capture:MaxConcurrentCaptures"] = "1",
            ["Capture:CaptureQueueLimit"] = "0",
        });
        var client = api.CreateClient();
        var endpoint = await Seed.EndpointAsync(fixture);

        // Hold the endpoint row lock, so the first capture waits inside the handler while
        // holding the only slot, like a capture stuck behind a slow database.
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Endpoint" WHERE "Id" = {endpoint.EndpointId} FOR UPDATE""");

        var waiting = client.PostAsync(endpoint.CaptureUrl("/waiting"), new StringContent("x"));
        await Task.Delay(300);
        var rejected = await client.PostAsync(endpoint.CaptureUrl("/rejected"), new StringContent("x"));
        await transaction.RollbackAsync();
        var waitingResponse = await waiting;
        var afterwards = await client.PostAsync(endpoint.CaptureUrl("/afterwards"), new StringContent("x"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), rejected.Headers.RetryAfter?.Delta);
        Assert.Equal(HttpStatusCode.OK, waitingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode); // the slot is free again
        Assert.Equal(["/waiting", "/afterwards"], (await Seed.StoredAsync(fixture, endpoint.EndpointId)).Select(r => r.Path));
    }

    [Fact]
    public async Task The_concurrency_limit_does_not_apply_to_the_api()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?>
        {
            ["Capture:MaxConcurrentCaptures"] = "1",
            ["Capture:CaptureQueueLimit"] = "0",
        });
        var user = await api.SignInAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => user.GetAsync("/api/endpoints")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}
