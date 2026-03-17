using System.Net;
using Microsoft.AspNetCore.Hosting;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Hosting;

[Collection(PostgresCollection.Name)]
public sealed class SpaHostingTests(PostgresFixture fixture) : IDisposable
{
    private readonly string _webRoot = Directory.CreateTempSubdirectory("wi-wwwroot-").FullName;

    private ApiFactory CreateApiWithSpa()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>spa</title>");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "app.js"), "console.log(1)");
        return fixture.CreateApi(new Dictionary<string, string?> { [WebHostDefaults.WebRootKey] = _webRoot });
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/endpoints/123")]
    public async Task Client_routes_serve_index_html(string url)
    {
        await using var api = CreateApiWithSpa();

        var response = await api.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>spa</title>", await response.Content.ReadAsStringAsync());
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task Static_assets_are_served()
    {
        await using var api = CreateApiWithSpa();

        var response = await api.CreateClient().GetAsync("/assets/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("console.log(1)", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/auth/nope")]
    [InlineData("/health/nope")]
    [InlineData("/hubs/nope")]
    [InlineData("/h")]
    public async Task Server_prefixes_never_fall_back_to_the_spa(string url)
    {
        await using var api = CreateApiWithSpa();

        var response = await api.CreateBrowserClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<title>spa</title>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Fallback_only_answers_GET()
    {
        await using var api = CreateApiWithSpa();

        var response = await api.CreateClient().PostAsync("/endpoints/123", new StringContent("x"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);
}
