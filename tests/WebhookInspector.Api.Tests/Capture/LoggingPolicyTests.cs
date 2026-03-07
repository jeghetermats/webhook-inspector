using System.Net.Http.Headers;
using System.Text;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Capture;

/// <summary>
/// PLAN.md section 7: header values, bodies, query strings, the capture token and the
/// path after it must never reach the logs. This covers the app's own logging with
/// its real log level configuration; platform logs are checked against the live service.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LoggingPolicyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Secrets_in_a_captured_request_never_appear_in_logs()
    {
        await using var api = fixture.CreateApi();
        var client = api.CreateClient();
        var endpoint = await Seed.EndpointAsync(fixture);
        var secret = "s3cret-" + Guid.NewGuid().ToString("N");

        var stored = new HttpRequestMessage(HttpMethod.Post, endpoint.CaptureUrl($"/{secret}-path?key={secret}-query"))
        {
            Content = new StringContent($$"""{"token":"{{secret}}-body"}""", Encoding.UTF8, "application/json"),
        };
        stored.Headers.Add("X-Secret", secret + "-header");
        stored.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret + "-auth");

        // Also cover the rejection paths: unknown token and oversized body.
        var unknown = new HttpRequestMessage(HttpMethod.Post, $"/h/{secret[..22]}/{secret}")
        {
            Content = new StringContent(secret),
        };
        var oversized = new HttpRequestMessage(HttpMethod.Post, endpoint.CaptureUrl($"/{secret}?q={secret}"))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(secret + new string('x', 300 * 1024))),
        };

        await client.SendAsync(stored);
        await client.SendAsync(unknown);
        await client.SendAsync(oversized);

        var entries = api.Logs.Entries;
        Assert.Contains(entries, e => e.Message.StartsWith("Captured request", StringComparison.Ordinal));
        foreach (var entry in entries)
        {
            var text = $"{entry.Category} {entry.Message} {entry.State} {entry.Exception}";
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(endpoint.Token, text, StringComparison.Ordinal);
        }
    }
}
