using System.Net;
using System.Net.Http.Json;
using WebhookInspector.Api.Api;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Management;

[Collection(PostgresCollection.Name)]
public class EndpointsApiTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Created_endpoint_is_listed_with_computed_usage()
    {
        var user = await fixture.Api.SignInAsync();

        var response = await user.PostAsync("/api/endpoints", new { name = "  GitHub hooks  " });
        var created = await response.Content.ReadFromJsonAsync<EndpointDto>();
        await fixture.Api.CaptureAsync(created!.CaptureToken, body: "12345");
        await fixture.Api.CaptureAsync(created.CaptureToken, body: "123");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/endpoints/{created.Id}", response.Headers.Location!.ToString());
        Assert.Equal("GitHub hooks", created.Name);
        var listed = Assert.Single(await user.ListEndpointsAsync());
        Assert.Equal(created.Id, listed.Id);
        Assert.Equal(2, listed.RequestCount);
        Assert.Equal(8, listed.BodyBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Name_is_required(string? name)
    {
        var user = await fixture.Api.SignInAsync();

        var response = await user.PostAsync("/api/endpoints", new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await user.ListEndpointsAsync());
    }

    [Fact]
    public async Task Name_longer_than_100_characters_is_rejected()
    {
        var user = await fixture.Api.SignInAsync();

        var ok = await user.PostAsync("/api/endpoints", new { name = new string('a', 100) });
        var tooLong = await user.PostAsync("/api/endpoints", new { name = new string('a', 101) });

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Parallel_creates_never_exceed_ten_endpoints()
    {
        var user = await fixture.Api.SignInAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 15)
            .Select(i => user.PostAsync("/api/endpoints", new { name = $"e{i}" })));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(10, (await user.ListEndpointsAsync()).Count);
    }

    [Fact]
    public async Task Rename_changes_only_the_name()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync("before");

        var response = await user.PatchAsync($"/api/endpoints/{endpoint.Id}", new { name = "after" });

        var renamed = await response.Content.ReadFromJsonAsync<EndpointDto>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(endpoint with { Name = "after" }, renamed);
    }

    [Fact]
    public async Task Delete_removes_the_endpoint_and_its_requests()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        await fixture.Api.CaptureAsync(endpoint.CaptureToken);

        var response = await user.DeleteAsync($"/api/endpoints/{endpoint.Id}");
        var capture = await fixture.Api.CreateClient().PostAsync($"/h/{endpoint.CaptureToken}", new StringContent("x"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await user.ListEndpointsAsync());
        Assert.Empty(await Seed.StoredAsync(fixture, endpoint.Id));
        Assert.Equal(HttpStatusCode.NotFound, capture.StatusCode);
    }

    [Fact]
    public async Task Rotating_the_token_disables_the_old_capture_url()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        await fixture.Api.CaptureAsync(endpoint.CaptureToken);
        var capture = fixture.Api.CreateClient();

        var response = await user.PostAsync($"/api/endpoints/{endpoint.Id}/rotate-token");
        var rotated = await response.Content.ReadFromJsonAsync<EndpointDto>();
        var oldUrl = await capture.PostAsync($"/h/{endpoint.CaptureToken}", new StringContent("x"));
        var newUrl = await capture.PostAsync($"/h/{rotated!.CaptureToken}", new StringContent("x"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(endpoint.CaptureToken, rotated.CaptureToken);
        Assert.Equal(HttpStatusCode.NotFound, oldUrl.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newUrl.StatusCode);
        Assert.Equal(2, (await user.ListEndpointsAsync()).Single().RequestCount); // history is kept
    }

    [Fact]
    public async Task Clear_removes_the_history_but_keeps_the_endpoint()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        var other = await user.CreateEndpointAsync("other");
        await fixture.Api.CaptureAsync(endpoint.CaptureToken);
        await fixture.Api.CaptureAsync(endpoint.CaptureToken);
        await fixture.Api.CaptureAsync(other.CaptureToken);

        var response = await user.DeleteAsync($"/api/endpoints/{endpoint.Id}/requests");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var endpoints = await user.ListEndpointsAsync();
        Assert.Equal(0, endpoints.Single(e => e.Id == endpoint.Id).RequestCount);
        Assert.Equal(1, endpoints.Single(e => e.Id == other.Id).RequestCount);
    }

    [Fact]
    public async Task Unknown_endpoint_returns_404()
    {
        var user = await fixture.Api.SignInAsync();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await user.PatchAsync($"/api/endpoints/{id}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.DeleteAsync($"/api/endpoints/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsync($"/api/endpoints/{id}/rotate-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.DeleteAsync($"/api/endpoints/{id}/requests")).StatusCode);
    }
}
