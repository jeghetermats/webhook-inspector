using System.Net;

namespace WebhookInspector.Api.Replay;

/// <summary>Turns a host name into addresses. Replaceable so tests can simulate DNS answers.</summary>
public interface IReplayHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsHostResolver : IReplayHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? Task.FromResult(new[] { literal })
            : Dns.GetHostAddressesAsync(host, ct);
}
