using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebhookInspector.Api.Replay;

namespace WebhookInspector.Api.Tests.Replay;

/// <summary>
/// A real HTTP server on 127.0.0.1 that plays the replay target. It has to be a real socket
/// server, because the code under test is the socket connection itself.
/// </summary>
public sealed class TargetServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TargetServer(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public int Port => BaseUri.Port;

    /// <summary>Paths requested so far, in order.</summary>
    public ConcurrentQueue<string> Hits { get; } = new();

    public Uri Url(string pathAndQuery) => new(BaseUri, pathAndQuery);

    public static async Task<TargetServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        TargetServer? server = null;
        app.Use(async (context, next) =>
        {
            server!.Hits.Enqueue(context.Request.Path + context.Request.QueryString);
            await next();
        });

        app.Map("/echo", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            context.Response.Headers["X-Target"] = "echo";
            await context.Response.WriteAsJsonAsync(new EchoResponse(
                context.Request.Method,
                context.Request.Headers.SelectMany(h => h.Value.Select(v => new[] { h.Key, v ?? string.Empty })).ToList(),
                Convert.ToBase64String(body.ToArray())));
        });
        app.Map("/redirect", () => Results.Redirect("/second"));
        app.Map("/second", () => Results.Text("followed"));
        app.Map("/big", () => Results.Bytes(Enumerable.Repeat((byte)'a', 100 * 1024).ToArray(), "text/plain"));
        app.Map("/slow", async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Results.Text("late");
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new TargetServer(app, new Uri(address));
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed record EchoResponse(string Method, List<string[]> Headers, string BodyBase64)
{
    public static EchoResponse Parse(byte[] json) =>
        JsonSerializer.Deserialize<EchoResponse>(json, JsonSerializerOptions.Web)!;

    public IEnumerable<string> HeaderNames => Headers.Select(h => h[0]);

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h[0].Equals(name, StringComparison.OrdinalIgnoreCase))?[1];
}

/// <summary>
/// The production policy, plus one exception: the test target's port on loopback. Tests that
/// need a successful replay must reach a local server, which the real policy correctly refuses.
/// Exists only in the test project; production has no switch for this.
/// </summary>
public sealed class AllowTargetServerPolicy(int port) : IReplayAddressPolicy
{
    private readonly PublicAddressPolicy _inner = new();

    public ConcurrentQueue<IPAddress> Checked { get; } = new();

    public string? Check(IPAddress address, int port1)
    {
        Checked.Enqueue(address);
        return IPAddress.IsLoopback(address) && port1 == port ? null : _inner.Check(address, port1);
    }
}

/// <summary>Answers DNS from a script, and counts how often each name was asked for.</summary>
public sealed class ScriptedResolver(Func<string, int, IPAddress[]?> answer) : IReplayHostResolver
{
    private readonly DnsHostResolver _literals = new();

    public ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var call = Calls.AddOrUpdate(host, 1, (_, n) => n + 1);
        if (IPAddress.TryParse(host.Trim('[', ']'), out _))
        {
            return _literals.ResolveAsync(host, ct);
        }

        return answer(host, call) is { } addresses
            ? Task.FromResult(addresses)
            : throw new SocketException((int)SocketError.HostNotFound);
    }
}
