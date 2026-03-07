using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WebhookInspector.Api.Capture;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests.Capture;

[Collection(PostgresCollection.Name)]
public class CaptureTests(PostgresFixture fixture)
{
    private readonly HttpClient _client = fixture.Api.CreateClient();

    [Theory]
    [InlineData("GET", false)]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    [InlineData("OPTIONS", false)]
    [InlineData("HEAD", false)]
    public async Task Every_supported_method_is_stored(string method, bool withBody)
    {
        var endpoint = await Seed.EndpointAsync(fixture);
        var request = new HttpRequestMessage(new HttpMethod(method), endpoint.CaptureUrl("/orders/42?a=1&a=2&b=x%20y"));
        request.Headers.Add("X-Test", "hello");
        if (withBody)
        {
            request.Content = new StringContent("""{"id":42}""", Encoding.UTF8, "application/json");
        }

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(await Seed.StoredAsync(fixture, endpoint.EndpointId));
        Assert.Equal(method, stored.Method);
        Assert.Equal("/orders/42", stored.Path);
        Assert.Equal("a=1&a=2&b=x%20y", stored.RawQuery);
        Assert.Contains(stored.Headers, h => h.Name == "X-Test" && h.Value == "hello");
        if (withBody)
        {
            Assert.Equal("""{"id":42}""", Encoding.UTF8.GetString(stored.Body));
            Assert.Equal(stored.Body.Length, stored.BodySize);
            Assert.StartsWith("application/json", stored.ContentType);
        }
        else
        {
            Assert.Empty(stored.Body);
        }
    }

    [Fact]
    public async Task Success_returns_ok_json()
    {
        var endpoint = await Seed.EndpointAsync(fixture);

        var response = await _client.PostAsync(endpoint.CaptureUrl(), new StringContent("x"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"ok":true}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Head_gets_no_body()
    {
        var endpoint = await Seed.EndpointAsync(fixture);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, endpoint.CaptureUrl()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("", "/", "")]
    [InlineData("/", "/", "")]
    [InlineData("?x=1", "/", "x=1")]
    [InlineData("/a/b/", "/a/b/", "")]
    [InlineData("/path%2Fwith%2Fencoded?q=%C3%A6", "/path%2Fwith%2Fencoded", "q=%C3%A6")]
    public async Task Path_and_query_are_kept_as_received(string suffix, string expectedPath, string expectedQuery)
    {
        var endpoint = await Seed.EndpointAsync(fixture);

        var response = await _client.GetAsync(endpoint.CaptureUrl(suffix));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(await Seed.StoredAsync(fixture, endpoint.EndpointId));
        Assert.Equal(expectedPath, stored.Path);
        Assert.Equal(expectedQuery, stored.RawQuery);
    }

    [Fact]
    public async Task Multiple_values_for_the_same_header_are_preserved()
    {
        var endpoint = await Seed.EndpointAsync(fixture);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint.CaptureUrl());
        request.Headers.TryAddWithoutValidation("X-Multi", ["one", "two"]);

        await _client.SendAsync(request);

        var stored = Assert.Single(await Seed.StoredAsync(fixture, endpoint.EndpointId));
        var values = stored.Headers.Where(h => h.Name == "X-Multi").Select(h => h.Value).ToList();
        Assert.Equal(["one", "two"], values);
    }

    [Fact]
    public async Task Binary_body_is_stored_byte_for_byte()
    {
        var endpoint = await Seed.EndpointAsync(fixture);
        var body = new byte[4096];
        Random.Shared.NextBytes(body);
        body[0] = 0x00;
        body[1] = 0xFF;
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        await _client.PostAsync(endpoint.CaptureUrl(), content);

        var stored = Assert.Single(await Seed.StoredAsync(fixture, endpoint.EndpointId));
        Assert.Equal(body, stored.Body);
        Assert.Equal(body.Length, stored.BodySize);
    }

    [Fact]
    public async Task Body_at_the_limit_is_accepted()
    {
        var endpoint = await Seed.EndpointAsync(fixture);

        var response = await _client.PostAsync(endpoint.CaptureUrl(), new ByteArrayContent(new byte[256 * 1024]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Body_over_the_limit_gives_413_and_is_not_stored()
    {
        var endpoint = await Seed.EndpointAsync(fixture);

        var response = await _client.PostAsync(endpoint.CaptureUrl(), new ByteArrayContent(new byte[256 * 1024 + 1]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await Seed.StoredAsync(fixture, endpoint.EndpointId));
    }

    [Fact]
    public async Task Chunked_body_over_the_limit_gives_413()
    {
        var endpoint = await Seed.EndpointAsync(fixture);
        // StreamContent over a non-seekable stream has no Content-Length, so the
        // limit has to be enforced while reading.
        var content = new StreamContent(new NonSeekableStream(new byte[300 * 1024]));
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint.CaptureUrl()) { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await Seed.StoredAsync(fixture, endpoint.EndpointId));
    }

    [Fact]
    public async Task Unknown_token_gives_404()
    {
        var response = await _client.PostAsync($"/h/{CaptureToken.Generate()}/x", new StringContent("x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/h/short")]
    [InlineData("/h/has.dots.in.it.and.is.22")]
    [InlineData("/h")]
    public async Task Malformed_token_gives_404(string url)
    {
        var response = await _client.PostAsync(url, new StringContent("x"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_method_is_not_captured()
    {
        var endpoint = await Seed.EndpointAsync(fixture);

        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod("TRACE"), endpoint.CaptureUrl()));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(await Seed.StoredAsync(fixture, endpoint.EndpointId));
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
