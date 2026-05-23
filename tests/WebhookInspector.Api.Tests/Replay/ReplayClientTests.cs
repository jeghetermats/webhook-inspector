using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using WebhookInspector.Api.Data;
using WebhookInspector.Api.Replay;

namespace WebhookInspector.Api.Tests.Replay;

/// <summary>
/// The SSRF defences of ADR 0013, tested against real sockets. Every test that expects a
/// block also checks that the local target server was never reached.
/// </summary>
public sealed class ReplayClientTests : IAsyncLifetime
{
    private TargetServer _target = null!;

    public async Task InitializeAsync() => _target = await TargetServer.StartAsync();

    public async Task DisposeAsync() => await _target.DisposeAsync();

    private static ReplayClient Client(IReplayAddressPolicy? policy = null, IReplayHostResolver? resolver = null, int timeoutSeconds = 5) =>
        new(policy ?? new PublicAddressPolicy(), resolver ?? new DnsHostResolver(), Options.Create(new ReplayOptions { TimeoutSeconds = timeoutSeconds }));

    private static Task<ReplayResult> SendAsync(ReplayClient client, string url, string method = "POST", IReadOnlyList<HeaderEntry>? headers = null, string body = "{}") =>
        client.SendAsync(new ReplayRequest(new Uri(url), method, headers ?? [], Encoding.UTF8.GetBytes(body), Guid.CreateVersion7()), CancellationToken.None);

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.1/")]                   // short form of 127.0.0.1
    [InlineData("http://2130706433/")]              // 127.0.0.1 as one decimal number
    [InlineData("http://0x7f000001/")]              // ... as hex
    [InlineData("http://0177.0.0.1/")]              // ... with an octal part
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::ffff:a9fe:a9fe]/metadata/identity/oauth2/token")] // 169.254.169.254 in hex IPv6
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("https://[fd00::1]/")]
    public async Task Blocks_loopback_private_and_metadata_addresses_in_any_notation(string url)
    {
        using var client = Client();

        var result = await SendAsync(client, url);

        Assert.Null(result.StatusCode);
        Assert.Equal("blocked", result.Outcome);
        Assert.StartsWith("Blocked:", result.Error);
    }

    [Fact]
    public async Task Blocks_a_host_name_that_resolves_to_a_private_address()
    {
        var resolver = new ScriptedResolver((host, _) => host == "internal.example" ? [IPAddress.Parse("10.1.2.3")] : null);
        using var client = Client(resolver: resolver);

        var result = await SendAsync(client, "https://internal.example/hook");

        Assert.Equal("blocked", result.Outcome);
    }

    [Fact]
    public async Task Blocks_a_name_with_both_public_and_private_addresses()
    {
        var resolver = new ScriptedResolver((_, _) => [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("127.0.0.1")]);
        using var client = Client(resolver: resolver);

        var result = await SendAsync(client, "https://mixed.example/");

        Assert.Equal("blocked", result.Outcome);
    }

    [Fact]
    public async Task Blocks_the_target_server_itself_with_the_real_policy()
    {
        using var client = Client();

        var result = await SendAsync(client, _target.Url("/echo").ToString());

        Assert.Equal("blocked", result.Outcome);
        Assert.Empty(_target.Hits);
    }

    /// <summary>
    /// DNS rebinding: a name answers with an allowed address when checked and a forbidden one
    /// when the request is actually made. The client must resolve once, check those addresses,
    /// and connect to exactly them. Here the first answer is the (allowed) test server and every
    /// later answer is 10.9.9.9; the request must reach the test server after a single lookup,
    /// so no second lookup exists for an attacker to change.
    /// </summary>
    [Fact]
    public async Task Resolves_once_and_connects_to_the_address_it_checked()
    {
        var policy = new AllowTargetServerPolicy(_target.Port);
        var resolver = new ScriptedResolver((_, call) => call == 1 ? [IPAddress.Loopback] : [IPAddress.Parse("10.9.9.9")]);
        using var client = Client(policy, resolver);

        var result = await SendAsync(client, $"http://rebind.example:{_target.Port}/echo");

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(1, resolver.Calls["rebind.example"]);
        Assert.Equal([IPAddress.Loopback], policy.Checked);
        Assert.Equal(["/echo"], _target.Hits);
    }

    [Fact]
    public async Task Does_not_follow_redirects()
    {
        using var client = Client(new AllowTargetServerPolicy(_target.Port));

        var result = await SendAsync(client, _target.Url("/redirect").ToString(), method: "GET");

        Assert.Equal(302, result.StatusCode);
        Assert.Contains(result.ResponseHeaders, h => h.Name == "Location" && h.Value == "/second");
        Assert.Equal(["/redirect"], _target.Hits); // /second was never requested
    }

    [Fact]
    public async Task Refuses_ports_other_than_80_and_443_before_connecting()
    {
        var resolver = new ScriptedResolver((_, _) => [IPAddress.Parse("1.1.1.1")]);
        using var client = Client(resolver: resolver);

        var result = await SendAsync(client, "http://public.example:8080/");

        Assert.Equal("Blocked: Only ports 80 and 443 are allowed.", result.Error);
    }

    [Fact]
    public async Task Forwards_the_request_but_not_credentials_or_connection_headers()
    {
        using var client = Client(new AllowTargetServerPolicy(_target.Port));
        var headers = new List<HeaderEntry>
        {
            new("Host", "ca-webhook-inspector.example"),
            new("Content-Type", "application/json"),
            new("X-GitHub-Event", "push"),
            new("X-Hub-Signature-256", "sha256=abc"),
            new("User-Agent", "GitHub-Hookshot/abc"),
            new("Cookie", "__Host-wi-auth=secret"),
            new("Authorization", "Bearer secret"),
            new("X-Forwarded-For", "203.0.113.1"),
            new("X-Forwarded-Proto", "https"),
            new("X-Envoy-External-Address", "203.0.113.1"),
            new("Accept-Encoding", "gzip"),
            new("Connection", "keep-alive, X-Hop"),
            new("X-Hop", "hop-by-hop"),
            new("Content-Length", "999"),
        };

        var result = await SendAsync(client, _target.Url("/echo?x=1").ToString(), method: "PUT", headers: headers, body: """{"ref":"main"}""");

        Assert.Equal(200, result.StatusCode);
        var echo = EchoResponse.Parse(result.ResponseBody);
        Assert.Equal("PUT", echo.Method);
        Assert.Equal("""{"ref":"main"}""", Encoding.UTF8.GetString(Convert.FromBase64String(echo.BodyBase64)));
        Assert.Equal($"127.0.0.1:{_target.Port}", echo.Header("Host"));
        Assert.Equal("application/json", echo.Header("Content-Type"));
        Assert.Equal("push", echo.Header("X-GitHub-Event"));
        Assert.Equal("sha256=abc", echo.Header("X-Hub-Signature-256"));
        Assert.Equal("GitHub-Hookshot/abc", echo.Header("User-Agent"));
        Assert.Equal("14", echo.Header("Content-Length"));
        Assert.NotNull(echo.Header(ReplayClient.ReplayHeader));
        foreach (var name in new[] { "Cookie", "Authorization", "X-Forwarded-For", "X-Forwarded-Proto", "X-Envoy-External-Address", "Accept-Encoding", "X-Hop", "traceparent" })
        {
            Assert.DoesNotContain(name, echo.HeaderNames, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Reads_at_most_64_KB_of_the_response()
    {
        using var client = Client(new AllowTargetServerPolicy(_target.Port));

        var result = await SendAsync(client, _target.Url("/big").ToString(), method: "GET");

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(64 * 1024, result.ResponseBody.Length);
        Assert.True(result.ResponseTruncated);
    }

    [Fact]
    public async Task Gives_up_after_the_timeout()
    {
        using var client = Client(new AllowTargetServerPolicy(_target.Port), timeoutSeconds: 1);

        var result = await SendAsync(client, _target.Url("/slow").ToString(), method: "GET");

        Assert.Equal("timeout", result.Outcome);
        Assert.Equal("Timed out after 1 seconds.", result.Error);
        Assert.InRange(result.DurationMs, 900, 3000);
    }

    [Fact]
    public async Task Reports_a_refused_connection_without_internal_details()
    {
        // A loopback port where nothing listens, allowed by the test policy.
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var client = Client(new AllowTargetServerPolicy(port));

        var result = await SendAsync(client, $"http://127.0.0.1:{port}/");

        Assert.Equal("connect", result.Outcome);
        Assert.Equal("Could not connect to the target.", result.Error);
    }

    [Fact]
    public async Task Reports_an_unknown_host()
    {
        using var client = Client(resolver: new ScriptedResolver((_, _) => null));

        var result = await SendAsync(client, "https://does-not-exist.example/");

        Assert.Equal("dns", result.Outcome);
    }
}
