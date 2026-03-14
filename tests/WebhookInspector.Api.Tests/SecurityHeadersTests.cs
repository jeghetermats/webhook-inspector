using WebhookInspector.Api.Security;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests;

[Collection(PostgresCollection.Name)]
public class SecurityHeadersTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/api/me")]
    [InlineData("/does-not-exist")]
    public async Task Every_response_carries_the_security_headers(string url)
    {
        var user = await fixture.Api.SignInAsync();

        var response = await user.GetAsync(url);

        Assert.Equal([SecurityHeaders.ContentSecurityPolicy], response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(["DENY"], response.Headers.GetValues("X-Frame-Options"));
    }

    [Fact]
    public async Task Capture_responses_carry_them_too_and_set_no_cookies()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();

        // Even when a browser that is signed in sends to the capture URL, the capture
        // pipeline neither reads nor refreshes the auth cookie.
        var response = await user.Client.PostAsync($"/h/{endpoint.CaptureToken}", new StringContent("x"));

        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
