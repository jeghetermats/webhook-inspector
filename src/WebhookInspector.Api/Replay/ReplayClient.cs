using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using WebhookInspector.Api.Data;

namespace WebhookInspector.Api.Replay;

/// <summary>What to send. Built from a captured request.</summary>
public sealed record ReplayRequest(
    Uri Target,
    string Method,
    IReadOnlyList<HeaderEntry> Headers,
    byte[] Body,
    Guid AttemptId);

/// <summary>What happened. Either a response (any status, including redirects) or an error.</summary>
public sealed record ReplayResult(
    int DurationMs,
    int? StatusCode,
    IReadOnlyList<HeaderEntry> ResponseHeaders,
    byte[] ResponseBody,
    bool ResponseTruncated,
    string? Error,
    string Outcome);

/// <summary>Thrown from the connect callback when the policy refuses an address.</summary>
public sealed class ReplayBlockedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// Sends replays without letting a user reach anything the server can reach but they cannot
/// (server-side request forgery, ADR 0013).
///
/// The address check happens inside <see cref="SocketsHttpHandler.ConnectCallback"/>: the host
/// is resolved once, every resulting address is checked, and the socket connects to exactly
/// those addresses. Checking a name beforehand and letting HttpClient resolve it again would
/// leave a gap that DNS rebinding can use (first answer public, second answer 127.0.0.1).
/// Redirects are not followed, no proxy is used (a proxy would hide the real target address
/// from the callback), and nothing from this server's own context is added to the request.
/// </summary>
public sealed class ReplayClient : IDisposable
{
    public const string ReplayHeader = "X-Webhook-Inspector-Replay";

    /// <summary>
    /// Not forwarded: the target's own Host, credentials that were meant for this service,
    /// headers that only describe the original connection, and Accept-Encoding (the response
    /// is shown as received, so ask for it uncompressed).
    /// </summary>
    private static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Authorization", "Proxy-Authorization", "Content-Length",
        "Transfer-Encoding", "Connection", "Keep-Alive", "TE", "Trailer", "Upgrade",
        "Proxy-Connection", "Expect", "Accept-Encoding", "Forwarded", "X-Real-IP", ReplayHeader,
    };

    private static readonly string[] DroppedPrefixes = ["X-Forwarded-", "X-Envoy-", "X-Original-"];

    private readonly HttpClient _client;
    private readonly ReplayOptions _options;
    private readonly IReplayAddressPolicy _policy;
    private readonly IReplayHostResolver _resolver;

    public ReplayClient(IReplayAddressPolicy policy, IReplayHostResolver resolver, IOptions<ReplayOptions> options)
    {
        _policy = policy;
        _resolver = resolver;
        _options = options.Value;

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
            MaxResponseHeadersLength = 32, // KB
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            // No traceparent header: the target learns nothing about this service's traces.
            ActivityHeadersPropagator = null,
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = await _resolver.ResolveAsync(host, ct);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        // All or nothing: a name that resolves to both a public and a private address is refused.
        foreach (var address in addresses)
        {
            if (_policy.Check(address, port) is { } reason)
            {
                throw new ReplayBlockedException(reason);
            }
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
        }

        throw last!;
    }

    public async Task<ReplayResult> SendAsync(ReplayRequest replay, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        // The replay target is user-chosen and may carry secrets in its URL, so the outgoing
        // call is kept out of telemetry entirely.
        using var suppressTelemetry = SuppressInstrumentationScope.Begin();
        try
        {
            using var request = BuildRequest(replay);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            var headers = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new HeaderEntry(h.Key, v)))
                .Take(100)
                .ToList();
            var (body, truncated) = await ReadLimitedAsync(response.Content, _options.MaxResponseBytes, timeout.Token);

            return new ReplayResult(Elapsed(stopwatch), (int)response.StatusCode, headers, body, truncated, null, "response");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(stopwatch, $"Timed out after {_options.TimeoutSeconds} seconds.", "timeout");
        }
        catch (HttpRequestException e)
        {
            return Classify(e, stopwatch);
        }
    }

    /// <summary>
    /// Error messages are chosen here, never copied from exceptions, so a failure cannot echo
    /// details about this server's network back to the user.
    /// </summary>
    private ReplayResult Classify(HttpRequestException e, Stopwatch stopwatch)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case ReplayBlockedException blocked:
                    return Failure(stopwatch, $"Blocked: {blocked.Reason}", "blocked");
                case AuthenticationException:
                    return Failure(stopwatch, "The TLS handshake with the target failed.", "tls");
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return Failure(stopwatch, "The host name could not be resolved.", "dns");
            }
        }

        return e.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => Failure(stopwatch, "The host name could not be resolved.", "dns"),
            HttpRequestError.ConnectionError => Failure(stopwatch, "Could not connect to the target.", "connect"),
            HttpRequestError.SecureConnectionError => Failure(stopwatch, "The TLS handshake with the target failed.", "tls"),
            _ => Failure(stopwatch, "The target did not send a valid HTTP response.", "invalid_response"),
        };
    }

    private static ReplayResult Failure(Stopwatch stopwatch, string error, string outcome) =>
        new(Elapsed(stopwatch), null, [], [], false, error, outcome);

    private static int Elapsed(Stopwatch stopwatch) => (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);

    internal static HttpRequestMessage BuildRequest(ReplayRequest replay)
    {
        var method = new HttpMethod(replay.Method);
        var request = new HttpRequestMessage(method, replay.Target);
        var sendsBody = replay.Body.Length > 0 && method != HttpMethod.Head;
        if (sendsBody)
        {
            request.Content = new ByteArrayContent(replay.Body);
        }

        // Headers named in Connection are hop-by-hop too (RFC 9110 section 7.6.1).
        var connectionOptions = replay.Headers
            .Where(h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var header in replay.Headers)
        {
            if (!IsForwarded(header.Name) || connectionOptions.Contains(header.Name))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value) && request.Content is not null)
            {
                // Content-Type and friends belong on the content.
                request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        request.Headers.TryAddWithoutValidation(ReplayHeader, replay.AttemptId.ToString());
        return request;
    }

    internal static bool IsForwarded(string name) =>
        !DroppedHeaders.Contains(name)
        && !DroppedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static async Task<(byte[] Body, bool Truncated)> ReadLimitedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[limit + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var truncated = total > limit;
        return (buffer[..Math.Min(total, limit)], truncated);
    }

    public void Dispose() => _client.Dispose();
}
