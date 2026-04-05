using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task Tokens_are_issued_and_accepted_over_plain_http_outside_production()
    {
        // Local development and the Playwright tests run on http://localhost. Browsers send
        // Secure cookies to localhost over http, but .NET's cookie jar does not, so this test
        // carries the cookies itself.
        var client = fixture.Api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = false,
        });
        var cookies = new Dictionary<string, string>();
        void Remember(HttpResponseMessage response)
        {
            foreach (var header in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
            {
                var pair = header.Split(';')[0].Split('=', 2);
                cookies[pair[0]] = pair[1];
            }
        }

        HttpRequestMessage WithCookies(HttpRequestMessage request)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
            return request;
        }

        Remember(await client.PostAsync($"/test/sign-in?githubId={TestIds.NextGitHubId()}&login=http-user", null));
        var tokenResponse = await client.SendAsync(WithCookies(new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery")));
        Remember(tokenResponse);

        var create = WithCookies(new HttpRequestMessage(HttpMethod.Post, "/api/endpoints")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { name = "over http" }),
        });
        create.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(cookies["XSRF-TOKEN"]));
        var created = await client.SendAsync(create);

        Assert.Equal(HttpStatusCode.NoContent, tokenResponse.StatusCode);
        Assert.Contains("wi-af", cookies.Keys); // not __Host-, which would need Secure
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Production_uses_a_host_prefixed_https_only_cookie()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?> { ["TestSignIn:Enabled"] = "false" })
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var options = api.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>>().Value;

        Assert.Equal("__Host-wi-af", options.Cookie.Name);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
    }
}
