using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using WebhookInspector.Api.Api;
using WebhookInspector.Api.Replay;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Replay;

[Collection(PostgresCollection.Name)]
public sealed class ReplayApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private TargetServer _target = null!;

    public async Task InitializeAsync() => _target = await TargetServer.StartAsync();

    public async Task DisposeAsync() => await _target.DisposeAsync();

    /// <summary>The app with the test server's loopback port allowed (see <see cref="AllowTargetServerPolicy"/>).</summary>
    private WebApplicationFactory<Program> ApiReachingTarget(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null) =>
        fixture.CreateApi(settings).WithWebHostBuilder(builder => builder.ConfigureServices(s =>
        {
            s.AddSingleton<IReplayAddressPolicy>(new AllowTargetServerPolicy(_target.Port));
            services?.Invoke(s);
        }));

    private static async Task<(TestUser User, Guid RequestId)> CaptureOneAsync(WebApplicationFactory<Program> api, string body = """{"ref":"main"}""")
    {
        var user = await api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        var capture = new HttpRequestMessage(HttpMethod.Post, $"/h/{endpoint.CaptureToken}/github?x=1")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        capture.Headers.Add("X-GitHub-Event", "push");
        (await api.CreateClient().SendAsync(capture)).EnsureSuccessStatusCode();
        var page = await user.ListRequestsAsync(endpoint.Id);
        return (user, page.Items.Single().Id);
    }

    private static async Task<HttpResponseMessage> ReplayAsync(TestUser user, Guid requestId, string? targetUrl) =>
        await user.PostAsync($"/api/requests/{requestId}/replays", new { targetUrl });

    [Fact]
    public async Task Replays_the_captured_request_and_stores_the_response()
    {
        await using var api = ApiReachingTarget();
        var (user, requestId) = await CaptureOneAsync(api);

        var response = await ReplayAsync(user, requestId, _target.Url("/echo?from=replay").ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var attempt = (await response.Content.ReadFromJsonAsync<ReplayAttemptDto>())!;
        Assert.Equal(200, attempt.StatusCode);
        Assert.Null(attempt.Error);
        Assert.Equal(requestId, attempt.RequestId);
        Assert.Contains(attempt.ResponseHeaders, h => h.Name == "X-Target" && h.Value == "echo");

        var echo = EchoResponse.Parse(Convert.FromBase64String(attempt.ResponseBodyBase64));
        Assert.Equal("POST", echo.Method);
        Assert.Equal("push", echo.Header("X-GitHub-Event"));
        Assert.Equal("""{"ref":"main"}""", Encoding.UTF8.GetString(Convert.FromBase64String(echo.BodyBase64)));
        Assert.Equal(["/echo?from=replay"], _target.Hits);

        var listed = await user.GetJsonAsync<List<ReplayAttemptDto>>($"/api/requests/{requestId}/replays");
        Assert.Equal(attempt.Id, Assert.Single(listed!).Id);
    }

    [Fact]
    public async Task A_blocked_target_is_stored_as_a_failed_attempt_and_never_contacted()
    {
        var (user, requestId) = await CaptureOneAsync(fixture.Api);

        var response = await ReplayAsync(user, requestId, _target.Url("/echo").ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var attempt = (await response.Content.ReadFromJsonAsync<ReplayAttemptDto>())!;
        Assert.Null(attempt.StatusCode);
        Assert.StartsWith("Blocked:", attempt.Error);
        Assert.Empty(_target.Hits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.com/")]
    public async Task Rejects_targets_that_are_not_plain_http_urls(string? targetUrl)
    {
        var (user, requestId) = await CaptureOneAsync(fixture.Api);

        var response = await ReplayAsync(user, requestId, targetUrl);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var listed = await user.GetJsonAsync<List<ReplayAttemptDto>>($"/api/requests/{requestId}/replays");
        Assert.Empty(listed!);
    }

    [Fact]
    public async Task Another_users_request_cannot_be_replayed_or_listed()
    {
        await using var api = ApiReachingTarget();
        var (owner, requestId) = await CaptureOneAsync(api);
        await ReplayAsync(owner, requestId, _target.Url("/echo").ToString());
        var stranger = await api.SignInAsync();

        var replay = await ReplayAsync(stranger, requestId, _target.Url("/echo?stranger").ToString());
        var list = await stranger.GetAsync($"/api/requests/{requestId}/replays");

        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.DoesNotContain("/echo?stranger", _target.Hits);
    }

    [Fact]
    public async Task Replays_are_rate_limited_per_user()
    {
        await using var api = ApiReachingTarget(new Dictionary<string, string?> { ["Replay:PermitsPerUserPerMinute"] = "2" });
        var (user, requestId) = await CaptureOneAsync(api);
        var (other, otherRequestId) = await CaptureOneAsync(api);

        var first = await ReplayAsync(user, requestId, _target.Url("/echo").ToString());
        var second = await ReplayAsync(user, requestId, _target.Url("/echo").ToString());
        var third = await ReplayAsync(user, requestId, _target.Url("/echo").ToString());
        var otherUser = await ReplayAsync(other, otherRequestId, _target.Url("/echo").ToString());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.Created, otherUser.StatusCode);
        Assert.Equal(3, _target.Hits.Count);
    }

    [Fact]
    public async Task Keeps_only_the_newest_attempts_listed_newest_first()
    {
        await using var api = ApiReachingTarget(new Dictionary<string, string?> { ["Replay:MaxAttemptsPerRequest"] = "3" });
        var (user, requestId) = await CaptureOneAsync(api);

        for (var i = 1; i <= 5; i++)
        {
            (await ReplayAsync(user, requestId, _target.Url($"/echo?n={i}").ToString())).EnsureSuccessStatusCode();
        }

        var listed = await user.GetJsonAsync<List<ReplayAttemptDto>>($"/api/requests/{requestId}/replays");
        Assert.Equal(["n=5", "n=4", "n=3"], listed!.Select(a => new Uri(a.TargetUrl).Query.TrimStart('?')));
    }

    [Fact]
    public async Task Attempts_are_deleted_with_their_captured_request()
    {
        await using var api = ApiReachingTarget();
        var (user, requestId) = await CaptureOneAsync(api);
        await ReplayAsync(user, requestId, _target.Url("/echo").ToString());

        (await user.DeleteAsync($"/api/requests/{requestId}")).EnsureSuccessStatusCode();

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.ReplayAttempts.AnyAsync(a => a.CapturedRequestId == requestId));
    }

    [Fact]
    public async Task The_target_url_stays_out_of_logs_and_telemetry()
    {
        var exported = new List<Activity>();
        await using var api = ApiReachingTarget(services: s =>
            s.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddHttpClientInstrumentation().AddInMemoryExporter(exported)));
        var (user, requestId) = await CaptureOneAsync(api);
        var secret = "replay-secret-" + Guid.NewGuid().ToString("N");

        var response = await ReplayAsync(user, requestId, _target.Url($"/echo/{secret}").ToString());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Control: an ordinary outgoing call from the same process is traced, so the
        // instrumentation is active and the replay is missing because it was suppressed.
        using (var plain = new HttpClient())
        {
            await plain.GetAsync(_target.Url("/echo/control-call"));
        }

        await Task.Delay(300);
        List<Activity> spans;
        lock (exported)
        {
            spans = exported.ToList();
        }

        Assert.Contains(spans, s => s.Kind == ActivityKind.Client && (s.GetTagItem("url.full") as string ?? "").Contains("/echo/control-call"));
        // The test target runs in this process too; its own incoming-request spans are not the app's.
        var appSpans = spans.Where(s => !(s.Kind == ActivityKind.Server && Convert.ToString(s.GetTagItem("server.port")) == _target.Port.ToString())).ToList();
        Assert.Contains(spans, s => s.Kind == ActivityKind.Server && Describe(s).Contains(secret, StringComparison.Ordinal)); // the target did receive it
        Assert.DoesNotContain(appSpans, s => Describe(s).Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(api.Services.GetRequiredService<TestLogSink>().Entries, e => e.Message.Contains(secret, StringComparison.Ordinal));
        Assert.Contains(api.Services.GetRequiredService<TestLogSink>().Entries, e => e.Message.Contains("outcome response"));
    }

    private static string Describe(Activity span) =>
        string.Join(" ", new[] { span.DisplayName, span.OperationName }
            .Concat(span.TagObjects.Select(t => $"{t.Key}={t.Value}")));
}
