using System.Net.Http.Json;
using WebhookInspector.Api.Api;

namespace WebhookInspector.Api.Tests.Infrastructure;

public static class ApiHelpers
{
    public static async Task<EndpointDto> CreateEndpointAsync(this TestUser user, string name = "test endpoint")
    {
        var response = await user.PostAsync("/api/endpoints", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EndpointDto>())!;
    }

    public static async Task CaptureAsync(this ApiFactory api, string token, string pathAndQuery = "/", string body = "{}", string method = "POST")
    {
        var request = new HttpRequestMessage(new HttpMethod(method), $"/h/{token}{pathAndQuery}");
        if (method is not ("GET" or "HEAD"))
        {
            request.Content = new StringContent(body);
        }

        var response = await api.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<List<EndpointDto>> ListEndpointsAsync(this TestUser user) =>
        (await user.GetJsonAsync<List<EndpointDto>>("/api/endpoints"))!;

    public static async Task<RequestPageDto> ListRequestsAsync(this TestUser user, Guid endpointId, string query = "") =>
        (await user.GetJsonAsync<RequestPageDto>($"/api/endpoints/{endpointId}/requests{query}"))!;
}
