using System.Net;
using System.Text;
using WebhookInspector.Api.Api;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Management;

[Collection(PostgresCollection.Name)]
public class RequestsApiTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Cursor_pagination_returns_every_request_once_newest_first()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        // Identical timestamps force the Id tie-breaker to do its job.
        await Seed.RequestsAsync(fixture, endpoint.Id, 120, receivedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var pages = new List<RequestPageDto>();
        string? cursor = null;
        do
        {
            var page = await user.ListRequestsAsync(endpoint.Id, cursor is null ? "" : $"?cursor={cursor}");
            pages.Add(page);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal([50, 50, 20], pages.Select(p => p.Items.Count));
        var all = pages.SelectMany(p => p.Items).ToList();
        Assert.Equal(120, all.Select(r => r.Id).Distinct().Count());
        var stored = (await Seed.StoredAsync(fixture, endpoint.Id)).Select(r => r.Id).ToHashSet();
        Assert.True(stored.SetEquals(all.Select(r => r.Id)));
    }

    [Fact]
    public async Task New_requests_arriving_between_pages_do_not_shift_the_next_page()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        for (var i = 0; i < 6; i++)
        {
            await fixture.Api.CaptureAsync(endpoint.CaptureToken, $"/old/{i}");
        }

        var first = await user.ListRequestsAsync(endpoint.Id, "?limit=3");
        for (var i = 0; i < 4; i++)
        {
            await fixture.Api.CaptureAsync(endpoint.CaptureToken, $"/new/{i}");
        }

        var second = await user.ListRequestsAsync(endpoint.Id, $"?limit=3&cursor={first.NextCursor}");

        Assert.Equal(["/old/5", "/old/4", "/old/3"], first.Items.Select(r => r.Path));
        Assert.Equal(["/old/2", "/old/1", "/old/0"], second.Items.Select(r => r.Path));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Filters_on_method_path_prefix_and_time()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        await fixture.Api.CaptureAsync(endpoint.CaptureToken, "/github/push", method: "POST");
        await fixture.Api.CaptureAsync(endpoint.CaptureToken, "/github/ping", method: "GET");
        await fixture.Api.CaptureAsync(endpoint.CaptureToken, "/stripe/charge", method: "POST");
        await fixture.Api.CaptureAsync(endpoint.CaptureToken, "/50%25_off", method: "PUT");

        var posts = await user.ListRequestsAsync(endpoint.Id, "?method=post");
        var github = await user.ListRequestsAsync(endpoint.Id, "?pathPrefix=/github");
        var githubPosts = await user.ListRequestsAsync(endpoint.Id, "?pathPrefix=/github&method=POST");
        var likeWildcards = await user.ListRequestsAsync(endpoint.Id, $"?pathPrefix={Uri.EscapeDataString("/50%")}");
        var future = await user.ListRequestsAsync(endpoint.Id, $"?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"))}");
        var past = await user.ListRequestsAsync(endpoint.Id, $"?to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"))}");
        var recent = await user.ListRequestsAsync(endpoint.Id, $"?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"))}");

        Assert.Equal(["/stripe/charge", "/github/push"], posts.Items.Select(r => r.Path));
        Assert.Equal(["/github/ping", "/github/push"], github.Items.Select(r => r.Path));
        Assert.Equal(["/github/push"], githubPosts.Items.Select(r => r.Path));
        Assert.Equal(["/50%25_off"], likeWildcards.Items.Select(r => r.Path)); // % is literal, not a LIKE wildcard
        Assert.Empty(future.Items);
        Assert.Empty(past.Items);
        Assert.Equal(4, recent.Items.Count);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?cursor=not-a-cursor")]
    [InlineData("?method=TRACE")]
    public async Task Invalid_list_parameters_return_400(string query)
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();

        var response = await user.GetAsync($"/api/endpoints/{endpoint.Id}/requests{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_shows_a_text_preview_but_none_for_binary_bodies()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        var capture = fixture.Api.CreateClient();
        await capture.PostAsync($"/h/{endpoint.CaptureToken}/binary", new ByteArrayContent([0x00, 0x01, 0xFF, 0xFE]));
        await capture.PostAsync($"/h/{endpoint.CaptureToken}/long", new StringContent(new string('æ', 500))); // 2 bytes per char
        await capture.PostAsync($"/h/{endpoint.CaptureToken}/json", new StringContent("""{"event":"push"}"""));

        var items = (await user.ListRequestsAsync(endpoint.Id)).Items.ToDictionary(r => r.Path);

        Assert.Equal("""{"event":"push"}""", items["/json"].BodyPreview);
        Assert.Equal(new string('æ', BodyPreview.PrefixBytes / 2), items["/long"].BodyPreview);
        Assert.Equal(1000, items["/long"].BodySize);
        Assert.Null(items["/binary"].BodyPreview);
    }

    [Fact]
    public async Task Detail_returns_headers_query_and_the_full_body()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/h/{endpoint.CaptureToken}/hook?a=1&a=2")
        {
            Content = new StringContent("""{"ok":1}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Multi", ["one", "two"]);
        await fixture.Api.CreateClient().SendAsync(request);
        var id = (await user.ListRequestsAsync(endpoint.Id)).Items.Single().Id;

        var detail = await user.GetJsonAsync<RequestDetailDto>($"/api/requests/{id}");

        Assert.NotNull(detail);
        Assert.Equal(endpoint.Id, detail.EndpointId);
        Assert.Equal("POST", detail.Method);
        Assert.Equal("/hook", detail.Path);
        Assert.Equal("a=1&a=2", detail.RawQuery);
        Assert.Equal(["one", "two"], detail.Headers.Where(h => h.Name == "X-Multi").Select(h => h.Value));
        Assert.Equal("""{"ok":1}""", Encoding.UTF8.GetString(Convert.FromBase64String(detail.BodyBase64)));
        Assert.StartsWith("application/json", detail.ContentType);
    }

    [Fact]
    public async Task Body_download_is_an_attachment_that_cannot_be_sniffed()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        var html = "<script>alert(1)</script>";
        await fixture.Api.CreateClient().PostAsync(
            $"/h/{endpoint.CaptureToken}", new StringContent(html, Encoding.UTF8, "text/html"));
        var id = (await user.ListRequestsAsync(endpoint.Id)).Items.Single().Id;

        var response = await user.GetAsync($"/api/requests/{id}/body");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(html, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delete_removes_one_request()
    {
        var user = await fixture.Api.SignInAsync();
        var endpoint = await user.CreateEndpointAsync();
        await fixture.Api.CaptureAsync(endpoint.CaptureToken, "/keep");
        await fixture.Api.CaptureAsync(endpoint.CaptureToken, "/delete");
        var target = (await user.ListRequestsAsync(endpoint.Id)).Items.Single(r => r.Path == "/delete");

        var response = await user.DeleteAsync($"/api/requests/{target.Id}");
        var again = await user.DeleteAsync($"/api/requests/{target.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(["/keep"], (await user.ListRequestsAsync(endpoint.Id)).Items.Select(r => r.Path));
    }
}
