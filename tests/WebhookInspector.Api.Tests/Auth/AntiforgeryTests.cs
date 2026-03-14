using System.Net;
using WebhookInspector.Api.Api;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class AntiforgeryTests(PostgresFixture fixture)
{
    public static TheoryData<string, string> StateChangingRoutes => new()
    {
        { "POST", "/api/endpoints" },
        { "PATCH", "/api/endpoints/{endpoint}" },
        { "DELETE", "/api/endpoints/{endpoint}" },
        { "POST", "/api/endpoints/{endpoint}/rotate-token" },
        { "DELETE", "/api/endpoints/{endpoint}/requests" },
        { "DELETE", "/api/requests/{request}" },
        { "POST", "/auth/logout" },
    };

    [Theory]
    [MemberData(nameof(StateChangingRoutes))]
    public async Task State_changing_requests_without_a_token_are_rejected(string method, string route)
    {
        var user = await fixture.Api.SignInAsync();
        var (url, endpoint) = await PrepareAsync(user, route);

        var response = await user.SendAsync(new HttpMethod(method), url, new { name = "changed" }, antiforgery: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUnchangedAsync(user, endpoint);
    }

    [Theory]
    [MemberData(nameof(StateChangingRoutes))]
    public async Task A_token_issued_to_another_user_is_rejected(string method, string route)
    {
        var user = await fixture.Api.SignInAsync();
        var other = await fixture.Api.SignInAsync();
        var (url, endpoint) = await PrepareAsync(user, route);

        var response = await user.SendAsync(new HttpMethod(method), url, new { name = "changed" }, token: other.AntiforgeryToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUnchangedAsync(user, endpoint);
    }

    [Fact]
    public async Task A_garbage_token_is_rejected()
    {
        var user = await fixture.Api.SignInAsync();

        var response = await user.SendAsync(HttpMethod.Post, "/api/endpoints", new { name = "x" }, token: "not-a-token");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await user.ListEndpointsAsync());
    }

    private async Task<(string Url, EndpointDto Endpoint)> PrepareAsync(TestUser user, string route)
    {
        var endpoint = await user.CreateEndpointAsync("original");
        await fixture.Api.CaptureAsync(endpoint.CaptureToken);
        var request = (await user.ListRequestsAsync(endpoint.Id)).Items.Single();
        var url = route.Replace("{endpoint}", endpoint.Id.ToString()).Replace("{request}", request.Id.ToString());
        return (url, endpoint);
    }

    private static async Task AssertUnchangedAsync(TestUser user, EndpointDto endpoint)
    {
        var endpoints = await user.ListEndpointsAsync();
        var current = Assert.Single(endpoints); // nothing created or deleted
        Assert.Equal("original", current.Name);
        Assert.Equal(endpoint.CaptureToken, current.CaptureToken);
        Assert.Equal(1, current.RequestCount);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/me")).StatusCode); // still signed in
    }
}
