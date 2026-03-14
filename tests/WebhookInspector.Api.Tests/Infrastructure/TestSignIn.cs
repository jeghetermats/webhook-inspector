using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Security;

namespace WebhookInspector.Api.Tests.Infrastructure;

/// <summary>
/// Test-only stand-in for the GitHub callback: POST /test/login?githubId=..&amp;login=..
/// runs the same user upsert and issues the same auth cookie as a real GitHub sign-in.
/// Registered by <see cref="ApiFactory"/> only, so it never exists in the app itself.
/// </summary>
public sealed class TestSignInStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (!context.Request.Path.Equals("/test/login"))
            {
                await nextMiddleware(context);
                return;
            }

            var gitHubId = long.Parse(context.Request.Query["githubId"].ToString());
            var login = context.Request.Query["login"].ToString();
            var users = context.RequestServices.GetRequiredService<UserService>();
            var userId = await users.UpsertFromGitHubAsync(gitHubId, login, context.RequestAborted);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                AppPrincipal.Create(userId, gitHubId, login));
            await context.Response.WriteAsJsonAsync(new SignInResult(userId));
        });
        next(app);
    };

    public sealed record SignInResult(Guid UserId);
}

/// <summary>A signed-in browser session: cookies plus the antiforgery request token.</summary>
public sealed class TestUser(HttpClient client, Guid userId, long gitHubId, string login, string antiforgeryToken)
{
    public HttpClient Client { get; } = client;

    public Guid UserId { get; } = userId;

    public long GitHubId { get; } = gitHubId;

    public string Login { get; } = login;

    public string AntiforgeryToken { get; } = antiforgeryToken;

    public Task<HttpResponseMessage> GetAsync(string url) => Client.GetAsync(url);

    public Task<T?> GetJsonAsync<T>(string url) => Client.GetFromJsonAsync<T>(url);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? json = null, bool antiforgery = true, string? token = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }

        if (antiforgery)
        {
            request.Headers.Add(AntiforgerySetup.HeaderName, token ?? AntiforgeryToken);
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> PostAsync(string url, object? json = null) => SendAsync(HttpMethod.Post, url, json);

    public Task<HttpResponseMessage> PatchAsync(string url, object? json = null) => SendAsync(HttpMethod.Patch, url, json);

    public Task<HttpResponseMessage> DeleteAsync(string url) => SendAsync(HttpMethod.Delete, url);
}

public static class TestSignIn
{
    public static HttpClient CreateBrowserClient(this ApiFactory api) =>
        api.CreateClient(new WebApplicationFactoryClientOptions
        {
            // https, so the Secure auth and antiforgery cookies are sent back.
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
            AllowAutoRedirect = false,
        });

    public static async Task<TestUser> SignInAsync(this ApiFactory api, string? login = null, long? gitHubId = null)
    {
        var client = api.CreateBrowserClient();
        var id = gitHubId ?? TestIds.NextGitHubId();
        login ??= $"user{id}";

        var signIn = await client.PostAsync($"/test/login?githubId={id}&login={Uri.EscapeDataString(login)}", null);
        signIn.EnsureSuccessStatusCode();
        var result = await signIn.Content.ReadFromJsonAsync<TestSignInStartupFilter.SignInResult>();

        var tokenResponse = await client.GetAsync("/api/antiforgery");
        tokenResponse.EnsureSuccessStatusCode();
        var token = GetCookieValue(tokenResponse, AntiforgerySetup.RequestTokenCookieName)
            ?? throw new InvalidOperationException("No XSRF-TOKEN cookie issued.");

        return new TestUser(client, result!.UserId, id, login, token);
    }

    public static string? GetSetCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

    public static string? GetCookieValue(HttpResponseMessage response, string name)
    {
        var header = GetSetCookie(response, name);
        if (header is null)
        {
            return null;
        }

        var value = header[(name.Length + 1)..];
        var end = value.IndexOf(';');
        return Uri.UnescapeDataString(end < 0 ? value : value[..end]);
    }
}
