using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using WebhookInspector.Api.Telemetry;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Capture;

/// <summary>
/// PLAN.md section 7: a unique secret sent in body, headers, query and path must not appear
/// in exported OpenTelemetry data. Uses the app's real telemetry setup with an in-memory
/// exporter added after it, the same position the Azure Monitor exporter has.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TelemetryRedactionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Exported_spans_never_contain_capture_secrets()
    {
        var exported = new List<Activity>();
        await using var api = fixture.CreateApi().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(exported))));
        var endpoint = await Seed.EndpointAsync(fixture);
        var secret = "s3cret-" + Guid.NewGuid().ToString("N");
        var client = api.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/h/{endpoint.Token}/{secret}-path?key={secret}-query")
        {
            Content = new StringContent($$"""{"token":"{{secret}}-body"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Secret", secret + "-header");
        request.Headers.UserAgent.ParseAdd($"agent-{secret}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        await client.SendAsync(request);
        await client.GetAsync($"/h/{secret[..22]}/{secret}?{secret}"); // unknown token
        await client.GetAsync($"/h/{secret}");                         // malformed token

        var spans = await WaitForAsync(exported, count: 3);

        var captureSpans = spans.Where(s => s.Kind == ActivityKind.Server).ToList();
        Assert.Equal(3, captureSpans.Count);
        foreach (var span in spans)
        {
            var text = Describe(span);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(endpoint.Token, text, StringComparison.Ordinal);
        }

        Assert.All(captureSpans, s => Assert.Equal(CaptureRedactionProcessor.RedactedPath, s.GetTagItem("url.path")));
        Assert.All(captureSpans, s => Assert.NotNull(s.GetTagItem("http.response.status_code"))); // still useful
    }

    [Fact]
    public async Task Other_routes_keep_their_path_and_health_probes_are_not_traced()
    {
        var exported = new List<Activity>();
        await using var api = fixture.CreateApi().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(exported))));
        var client = api.CreateClient();

        await client.GetAsync("/health/live");
        await client.GetAsync("/api/me");

        var spans = await WaitForAsync(exported, count: 1);
        var span = Assert.Single(spans);
        Assert.Equal("/api/me", span.GetTagItem("url.path"));
    }

    private static async Task<List<Activity>> WaitForAsync(List<Activity> exported, int count)
    {
        // Server spans end just after the response is written, so give them a moment.
        for (var i = 0; i < 50; i++)
        {
            lock (exported)
            {
                if (exported.Count >= count)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        await Task.Delay(100); // let any unexpected extra spans arrive too
        lock (exported)
        {
            return exported.ToList();
        }
    }

    private static string Describe(Activity span) =>
        string.Join(
            " ",
            new[] { span.DisplayName, span.OperationName, span.StatusDescription ?? string.Empty }
                .Concat(span.TagObjects.Select(t => $"{t.Key}={t.Value}"))
                .Concat(span.Events.SelectMany(e => e.Tags.Select(t => $"{e.Name}:{t.Key}={t.Value}")))
                .Concat(span.Baggage.Select(b => $"{b.Key}={b.Value}")));
}
