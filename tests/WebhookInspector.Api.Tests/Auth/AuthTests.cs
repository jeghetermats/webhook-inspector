using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebhookInspector.Api.Api;
using WebhookInspector.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class AuthTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("GET", "/api/me")]
    [InlineData("GET", "/api/endpoints")]
    [InlineData("GET", "/api/antiforgery")]
    [InlineData("POST", "/api/endpoints")]
    public async Task Api_requires_sign_in(string method, string url)
    {
        var client = fixture.Api.CreateBrowserClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_signed_in_user()
    {
        var user = await fixture.Api.SignInAsync("octocat");

        var me = await user.GetJsonAsync<MeDto>("/api/me");

        Assert.Equal(new MeDto(user.UserId, "octocat"), me);
    }

    [Fact]
    public async Task Auth_cookie_is_HttpOnly_Secure_and_SameSite_Lax()
    {
        var client = fixture.Api.CreateBrowserClient();

        var response = await client.PostAsync($"/test/sign-in?githubId={TestIds.NextGitHubId()}&login=a", null);

        var cookie = TestSignInClient.GetSetCookie(response, AuthSetup.AuthCookieName)?.ToLowerInvariant();
        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/;", cookie + ";");
        Assert.DoesNotContain("domain=", cookie);
    }

    [Fact]
    public async Task Signing_in_again_updates_the_login_instead_of_creating_a_user()
    {
        var gitHubId = TestIds.NextGitHubId();

        var first = await fixture.Api.SignInAsync("old-name", gitHubId);
        var second = await fixture.Api.SignInAsync("new-name", gitHubId);

        Assert.Equal(first.UserId, second.UserId);
        await using var db = fixture.CreateDbContext();
        var users = await db.Users.Where(u => u.GitHubId == gitHubId).ToListAsync();
        Assert.Equal("new-name", Assert.Single(users).Login);
    }

    [Fact]
    public async Task Parallel_first_sign_ins_create_one_user()
    {
        var gitHubId = TestIds.NextGitHubId();

        var users = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => fixture.Api.SignInAsync("same", gitHubId)));

        Assert.Single(users.Select(u => u.UserId).Distinct());
    }

    [Fact]
    public async Task Antiforgery_token_cookie_is_readable_by_javascript()
    {
        var user = await fixture.Api.SignInAsync();

        var response = await user.GetAsync("/api/antiforgery");

        var cookie = TestSignInClient.GetSetCookie(response, "XSRF-TOKEN")?.ToLowerInvariant();
        Assert.NotNull(cookie);
        Assert.DoesNotContain("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [Fact]
    public async Task Logout_requires_antiforgery_and_ends_the_session()
    {
        var user = await fixture.Api.SignInAsync();

        var withoutToken = await user.SendAsync(HttpMethod.Post, "/auth/logout", antiforgery: false);
        var stillSignedIn = await user.GetAsync("/api/me");
        var withToken = await user.SendAsync(HttpMethod.Post, "/auth/logout");
        var afterLogout = await user.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, withToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Login_without_GitHub_configuration_returns_503()
    {
        var response = await fixture.Api.CreateBrowserClient().GetAsync("/auth/login");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Login_redirects_to_GitHub_when_configured()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?>
        {
            ["Authentication:GitHub:ClientId"] = "test-client-id",
            ["Authentication:GitHub:ClientSecret"] = "test-client-secret",
        });

        var response = await api.CreateBrowserClient().GetAsync("/auth/login?returnUrl=/endpoints");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://github.com/login/oauth/authorize", location);
        Assert.Contains("client_id=test-client-id", location);
        Assert.Contains(Uri.EscapeDataString("https://localhost/signin-github"), location);
    }

    [Fact]
    public async Task A_failed_GitHub_callback_redirects_to_the_sign_in_page_instead_of_500()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?>
        {
            ["Authentication:GitHub:ClientId"] = "test-client-id",
            ["Authentication:GitHub:ClientSecret"] = "test-client-secret",
        });

        // No correlation cookie and a bogus state: the remote handler fails.
        var response = await api.CreateBrowserClient().GetAsync("/signin-github?code=abc&state=bogus");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?signin=failed", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/endpoints/1", "/endpoints/1")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("endpoints", "/")]
    public void Return_url_must_be_a_local_path(string? returnUrl, string expected)
    {
        Assert.Equal(expected, AuthSetup.SafeReturnUrl(returnUrl));
    }

    [Fact]
    public async Task Data_protection_keys_are_stored_in_postgres()
    {
        await fixture.Api.SignInAsync();

        await using var db = fixture.CreateDbContext();
        Assert.True(await db.DataProtectionKeys.AnyAsync());
    }

    [Fact]
    public async Task Sign_in_survives_an_app_restart()
    {
        HttpResponseMessage signIn;
        await using (var before = fixture.CreateApi())
        {
            signIn = await before.CreateBrowserClient()
                .PostAsync($"/test/sign-in?githubId={TestIds.NextGitHubId()}&login=restart", null);
        }

        var cookie = TestSignInClient.GetCookieValue(signIn, AuthSetup.AuthCookieName)!;
        await using var after = fixture.CreateApi();
        var client = after.CreateBrowserClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("Cookie", $"{AuthSetup.AuthCookieName}={cookie}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("restart", (await response.Content.ReadFromJsonAsync<MeDto>())!.Login);
    }

    [Fact]
    public async Task Test_sign_in_does_not_exist_unless_enabled()
    {
        await using var api = fixture.CreateApi(new Dictionary<string, string?> { ["TestSignIn:Enabled"] = "false" });

        var response = await api.CreateBrowserClient().PostAsync($"/test/sign-in?githubId={TestIds.NextGitHubId()}&login=x", null);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Null(TestSignInClient.GetSetCookie(response, AuthSetup.AuthCookieName));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Test_sign_in_refuses_to_start_outside_the_testing_environment(string environment)
    {
        using var api = fixture.CreateApi().WithWebHostBuilder(builder => builder.UseEnvironment(environment));

        var error = Assert.Throws<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains("TestSignIn:Enabled", error.Message);
    }
}

