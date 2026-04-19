using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using WebhookInspector.Api.Auth;
using WebhookInspector.Api.Live;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Live;

[Collection(PostgresCollection.Name)]
public sealed class RequestsHubTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<HubConnection> _connections = [];

    private HubConnection Connect(WebApplicationFactory<Program> api, string? authCookie)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"https://localhost{RequestsHub.Path}", options =>
            {
                // TestServer has no real sockets, so use long polling through its handler.
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (authCookie is not null)
                {
                    options.Headers["Cookie"] = $"{AuthSetup.AuthCookieName}={authCookie}";
                }
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private static ConcurrentQueue<JsonElement> Listen(HubConnection connection)
    {
        var received = new ConcurrentQueue<JsonElement>();
        connection.On<JsonElement>(RequestsHub.RequestReceived, e => received.Enqueue(e));
        return received;
    }

    private static async Task<JsonElement> WaitForAsync(ConcurrentQueue<JsonElement> received)
    {
        for (var i = 0; i < 100 && received.IsEmpty; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(received.TryDequeue(out var e), "no RequestReceived event arrived");
        return e;
    }

    [Fact]
    public async Task Connecting_requires_sign_in()
    {
        var connection = Connect(fixture.Api, authCookie: null);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task The_owner_is_notified_after_commit_with_metadata_only()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        var connection = Connect(fixture.Api, user.AuthCookie);
        var received = Listen(connection);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", endpoint.Id);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/h/{endpoint.CaptureToken}/push?secret-query=1")
        {
            Content = new StringContent("secret-body"),
        };
        request.Headers.Add("X-Secret", "secret-header");
        await fixture.Api.CreateClient().SendAsync(request);

        var e = await WaitForAsync(received);
        Assert.Equal(
            ["endpointId", "requestId", "method", "path", "receivedAt", "bodySize", "cursor"],
            e.EnumerateObject().Select(p => p.Name));
        Assert.Equal(endpoint.Id, e.GetProperty("endpointId").GetGuid());
        Assert.Equal("POST", e.GetProperty("method").GetString());
        Assert.Equal("/push", e.GetProperty("path").GetString());
        Assert.Equal(11, e.GetProperty("bodySize").GetInt32());
        Assert.DoesNotContain("secret", e.GetRawText(), StringComparison.Ordinal);

        // Sent after commit: whoever is told about the request can already load it.
        await using var db = fixture.CreateDbContext();
        Assert.True(await db.CapturedRequests.AnyAsync(r => r.Id == e.GetProperty("requestId").GetGuid()));
    }

    [Fact]
    public async Task The_event_cursor_continues_pagination_like_the_list_does()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        await fixture.Api.CaptureAsync(endpoint.CaptureToken, "/older");
        var connection = Connect(fixture.Api, user.AuthCookie);
        var received = Listen(connection);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", endpoint.Id);

        await fixture.Api.CaptureAsync(endpoint.CaptureToken, "/newer");
        var e = await WaitForAsync(received);

        var page = await user.ListRequestsAsync(endpoint.Id, $"?cursor={e.GetProperty("cursor").GetString()}");
        Assert.Equal(["/older"], page.Items.Select(r => r.Path));
    }

    [Fact]
    public async Task Another_user_cannot_subscribe_to_an_endpoint_or_receive_its_events()
    {
        var alice = await fixture.Api.SignInAsync();
        var bob = await fixture.Api.SignInAsync();
        var aliceEndpoint = await alice.CreateEndpointAsync();
        var bobEndpoint = await bob.CreateEndpointAsync();
        var connection = Connect(fixture.Api, bob.AuthCookie);
        var received = Listen(connection);
        await connection.StartAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", aliceEndpoint.Id));
        await connection.InvokeAsync("Subscribe", bobEndpoint.Id);
        await fixture.Api.CaptureAsync(aliceEndpoint.CaptureToken);
        await Task.Delay(500);

        Assert.Contains("Endpoint not found", error.Message);
        Assert.Empty(received);
    }

    [Fact]
    public async Task Rejected_captures_send_no_event()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        await Seed.RequestsAsync(fixture, endpoint.Id, 1000); // quota full
        var connection = Connect(fixture.Api, user.AuthCookie);
        var received = Listen(connection);
        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", endpoint.Id);

        var response = await fixture.Api.CreateClient().PostAsync($"/h/{endpoint.CaptureToken}", new StringContent("x"));
        await Task.Delay(500);

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Empty(received);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }
}
