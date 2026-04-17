using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Auth;

/// <summary>
/// Where authentication sits in the pipeline. Regression tests for a production bug: the
/// framework's own auth middleware ran before the forwarded headers were applied, so the
/// GitHub callback saw http and sent the wrong redirect_uri (redirect_uri_mismatch).
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuthPipelineTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_GitHub_callback_sees_the_scheme_from_the_ingress()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["Authentication:GitHub:ClientId"] = "test-client-id",
            ["Authentication:GitHub:ClientSecret"] = "test-client-secret",
        });
        // Plain http to the app, like the ingress does, with the original scheme in a header.
        var client = api.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://ca-test.example"),
            AllowAutoRedirect = false,
        });
        var request = new HttpRequestMessage(HttpMethod.Get, "/signin-github?code=abc&state=bogus");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var line = Assert.Single(api.Logs.Entries, e => e.Message.StartsWith("GitHub sign-in failed", StringComparison.Ordinal));
        Assert.Contains("Callback seen as https://ca-test.example/signin-github", line.Message);
    }

    [Fact]
    public async Task The_login_redirect_uses_the_scheme_from_the_ingress()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["Authentication:GitHub:ClientId"] = "test-client-id",
            ["Authentication:GitHub:ClientSecret"] = "test-client-secret",
        });
        var client = api.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://ca-test.example"),
            AllowAutoRedirect = false,
        });
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/login");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        Assert.Contains(Uri.EscapeDataString("https://ca-test.example/signin-github"), response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_capture_route_never_reads_the_session_cookie()
    {
        var validations = 0;
        await using var api = fixture.CreateApi().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
                {
                    var previous = options.Events.OnValidatePrincipal;
                    options.Events.OnValidatePrincipal = async context =>
                    {
                        Interlocked.Increment(ref validations);
                        await previous(context);
                    };
                })));
        var user = await api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        await user.GetAsync("/api/me"); // control: the API does read it
        var afterApi = validations;

        var capture = await user.Client.PostAsync($"/h/{endpoint.CaptureToken}", new StringContent("x"));

        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        Assert.True(afterApi > 0, "control failed: the API should validate the cookie");
        Assert.Equal(afterApi, validations);
    }
}
