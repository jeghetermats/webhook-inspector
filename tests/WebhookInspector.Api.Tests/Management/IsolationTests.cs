using System.Net;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Management;

/// <summary>
/// PLAN.md section 12: user B must get 404 (not 403) on every one of user A's resources,
/// and nothing of A's may change as a result of B's attempts.
/// </summary>
[Collection(PostgresCollection.Name)]
public class IsolationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Another_users_resources_are_invisible_and_untouchable()
    {
        var alice = await fixture.Api.SignInAsync("alice");
        var bob = await fixture.Api.SignInAsync("bob");
        var endpoint = await alice.CreateEndpointAsync("alice's");
        await fixture.Api.CaptureAsync(endpoint.CaptureToken, "/secret", "alice-only");
        var request = (await alice.ListRequestsAsync(endpoint.Id)).Items.Single();

        var attempts = new Dictionary<string, HttpResponseMessage>
        {
            ["list requests"] = await bob.GetAsync($"/api/endpoints/{endpoint.Id}/requests"),
            ["request detail"] = await bob.GetAsync($"/api/requests/{request.Id}"),
            ["body download"] = await bob.GetAsync($"/api/requests/{request.Id}/body"),
            ["delete request"] = await bob.DeleteAsync($"/api/requests/{request.Id}"),
            ["rename endpoint"] = await bob.PatchAsync($"/api/endpoints/{endpoint.Id}", new { name = "pwned" }),
            ["rotate token"] = await bob.PostAsync($"/api/endpoints/{endpoint.Id}/rotate-token"),
            ["clear history"] = await bob.DeleteAsync($"/api/endpoints/{endpoint.Id}/requests"),
            ["delete endpoint"] = await bob.DeleteAsync($"/api/endpoints/{endpoint.Id}"),
        };

        foreach (var (name, response) in attempts)
        {
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{name}: expected 404, got {(int)response.StatusCode}");
        }

        Assert.Empty(await bob.ListEndpointsAsync());

        var after = Assert.Single(await alice.ListEndpointsAsync());
        Assert.Equal(endpoint with { RequestCount = 1, BodyBytes = "alice-only".Length }, after);
        Assert.Equal(request.Id, (await alice.ListRequestsAsync(endpoint.Id)).Items.Single().Id);
    }

    [Fact]
    public async Task Each_user_lists_only_their_own_endpoints()
    {
        var alice = await fixture.Api.SignInAsync();
        var bob = await fixture.Api.SignInAsync();
        var a = await alice.CreateEndpointAsync("a");
        var b = await bob.CreateEndpointAsync("b");

        Assert.Equal([a.Id], (await alice.ListEndpointsAsync()).Select(e => e.Id));
        Assert.Equal([b.Id], (await bob.ListEndpointsAsync()).Select(e => e.Id));
    }

    [Fact]
    public async Task The_endpoint_limit_is_per_user()
    {
        var alice = await fixture.Api.SignInAsync();
        var bob = await fixture.Api.SignInAsync();
        for (var i = 0; i < 10; i++)
        {
            await alice.CreateEndpointAsync($"a{i}");
        }

        var aliceEleventh = await alice.PostAsync("/api/endpoints", new { name = "a10" });
        var bobFirst = await bob.PostAsync("/api/endpoints", new { name = "b0" });

        Assert.Equal(HttpStatusCode.Conflict, aliceEleventh.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bobFirst.StatusCode);
    }
}
