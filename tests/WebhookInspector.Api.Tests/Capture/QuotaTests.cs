using System.Net;
using Microsoft.EntityFrameworkCore;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Capture;

[Collection(PostgresCollection.Name)]
public class QuotaTests(PostgresFixture fixture)
{
    private readonly HttpClient _client = fixture.Api.CreateClient();

    [Fact]
    public async Task Parallel_requests_never_exceed_the_request_quota()
    {
        var endpoint = await Seed.EndpointAsync(fixture);
        await Seed.RequestsAsync(fixture, endpoint.EndpointId, 990); // 10 free slots

        var responses = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => _client.PostAsync(endpoint.CaptureUrl($"/{i}"), new StringContent("x"))));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(40, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(1000, (await Seed.StoredAsync(fixture, endpoint.EndpointId)).Count);
    }

    [Fact]
    public async Task Full_quota_returns_429_with_an_explanation()
    {
        var endpoint = await Seed.EndpointAsync(fixture);
        await Seed.RequestsAsync(fixture, endpoint.EndpointId, 1000);

        var response = await _client.PostAsync(endpoint.CaptureUrl(), new StringContent("x"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("Quota full", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Body_byte_quota_is_enforced()
    {
        const long quota = 50L * 1024 * 1024;
        var endpoint = await Seed.EndpointAsync(fixture);
        // One placeholder row that uses all but 10 bytes of the body quota.
        await Seed.RequestsAsync(fixture, endpoint.EndpointId, 1, bodySizeEach: (int)(quota - 10));

        var tooBig = await _client.PostAsync(endpoint.CaptureUrl(), new ByteArrayContent(new byte[11]));
        var fits = await _client.PostAsync(endpoint.CaptureUrl(), new ByteArrayContent(new byte[10]));
        var full = await _client.PostAsync(endpoint.CaptureUrl(), new ByteArrayContent(new byte[1]));
        var empty = await _client.PostAsync(endpoint.CaptureUrl(), new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.TooManyRequests, tooBig.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode); // a request without a body still fits
    }

    [Fact]
    public async Task Concurrent_captures_and_deletes_never_leave_the_endpoint_over_quota()
    {
        var endpoint = await Seed.EndpointAsync(fixture);
        await Seed.RequestsAsync(fixture, endpoint.EndpointId, 1000, receivedAt: DateTimeOffset.UtcNow.AddDays(-8));

        var deleted = 0;
        var captures = Enumerable.Range(0, 200)
            .Select(i => _client.PostAsync(endpoint.CaptureUrl($"/{i}"), new StringContent("x")))
            .ToList();

        // Deletes in the style of the cleanup job and user deletes: they only remove
        // CapturedRequest rows and never lock the endpoint row.
        var deletes = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var db = fixture.CreateDbContext();
            var rows = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM "CapturedRequest"
                WHERE "Id" IN (
                    SELECT "Id" FROM "CapturedRequest"
                    WHERE "EndpointId" = {endpoint.EndpointId} AND "ReceivedAt" < now() - interval '7 days'
                    ORDER BY "ReceivedAt" LIMIT 20)
                """);
            Interlocked.Add(ref deleted, rows);
        });

        var allCaptures = Task.WhenAll(captures);
        await Task.WhenAll(allCaptures, Task.WhenAll(deletes));
        var responses = await allCaptures;
        var stored = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        var remaining = (await Seed.StoredAsync(fixture, endpoint.EndpointId)).Count;
        Assert.Equal(200, stored + rejected);
        Assert.True(remaining <= 1000, $"Endpoint holds {remaining} requests, over the quota of 1000");
        Assert.Equal(1000 - deleted + stored, remaining); // computed usage matches the rows
    }
}
