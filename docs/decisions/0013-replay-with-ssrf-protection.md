# 0013: Replay with SSRF protection, checked at connect time

- Status: accepted
- Date: 2026-05-28

## Context

Replay sends a stored request to a URL the user chooses. That makes this server send requests on a user's behalf, which is server-side request forgery (SSRF) unless it is limited. From inside the Container App a request can reach things an outsider cannot:

- the Azure Instance Metadata Service at `169.254.169.254` and the managed identity endpoint, which hand out access tokens,
- `localhost` and the app's own ports,
- other apps and services in the Container Apps environment and any private network,
- third-party servers, with this service's IP address as the sender.

A check on the URL string is not enough. `127.0.0.1` can be written as `2130706433`, `0x7f000001`, `0177.0.0.1`, `127.1`, `[::1]` or `[::ffff:127.0.0.1]`. A host name can resolve to a private address. A public URL can redirect to a private one. And with DNS rebinding a name answers with a public address when it is checked and a private one when the request is made.

## Decision

The address is checked where it is used: in `SocketsHttpHandler.ConnectCallback`. The callback resolves the host once, checks every resulting address against the policy, and connects the socket to exactly those addresses. No separate lookup exists that an attacker could answer differently.

The policy allows only ports 80 and 443 and only public addresses. It blocks loopback, private ranges, link-local (including the metadata service), CGNAT, multicast, documentation and reserved ranges, and in IPv6 everything outside global unicast `2000::/3` plus the ranges inside it that embed IPv4 addresses (6to4, Teredo). IPv4-mapped IPv6 addresses are checked as the IPv4 address they map to. A name that resolves to both public and private addresses is refused.

Around that:

- **No redirects.** A `3xx` is returned to the user as the response. Following it would need the same check per hop, and showing it is more useful when debugging a webhook handler anyway.
- **No proxy.** With a proxy, the callback would only see the proxy's address.
- **Header policy.** `Host`, `Cookie`, `Authorization`, `Proxy-Authorization`, hop-by-hop headers (including those named in `Connection`), `Content-Length`, `Accept-Encoding` and forwarding headers added by the ingress are not sent. An `X-Webhook-Inspector-Replay` header identifies the request as a replay.
- **Limits.** 5 seconds per attempt, at most 64 KB of the response is read, 10 replays per user per minute and 8 at once. The last 20 attempts per request are kept and deleted with the request.
- **Fixed error messages.** Errors are mapped to a few categories (blocked, timeout, DNS, connect, TLS, invalid response) with messages written in code. Exception text is never returned, so a failure cannot describe this server's network.
- **No telemetry or logs with the target.** The target URL may carry secrets. The handler has no trace propagator, which also stops .NET from creating a client span, and the call runs inside `SuppressInstrumentationScope` as a second layer. Logs contain the attempt id, outcome, status and duration only.

## Alternatives considered

- **Resolve and check before sending, then let HttpClient connect.** Simple, and the common mistake: HttpClient resolves the name again, and that second answer is the one used (DNS rebinding).
- **Allow-list of target hosts per user.** Safer against abuse of third parties, but needs a verification flow (proving you own a domain) that is out of proportion here. The rate limit covers the abuse case well enough for this service.
- **An egress proxy or firewall that enforces the rules.** The right tool for a larger system, since it protects every outgoing call. Azure Firewall costs far more than the rest of this project, and the in-process check can be tested end to end.
- **Follow redirects with a check per hop.** Possible, since every hop goes through the same callback. Left out because it adds cases to test and is not needed for the use case.

## Consequences

- Replay cannot reach `localhost`, so a developer replays to a public URL such as a tunnel (ngrok, Cloudflare Tunnel) or a staging server, not directly to their laptop. The UI says so.
- Ports other than 80 and 443 are refused, so a staging server on port 8080 is not reachable. This keeps the service from being used as a port scanner.
- Tests that need a successful replay must reach a local test server, which the real policy refuses. They replace the policy through dependency injection with one that allows exactly the test server's loopback port. Production has no setting that weakens the policy.
- `ReplayClientTests` covers every bypass above against real sockets, including the address notations, a name resolving to a private address, a rebinding resolver (it checks that the host is resolved exactly once and the checked address is the one used) and a redirect to a second path. Disabling the address check makes 19 of its 25 tests fail.
