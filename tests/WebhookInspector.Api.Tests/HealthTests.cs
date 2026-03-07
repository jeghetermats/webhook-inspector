using System.Net;
using WebhookInspector.Api.Tests.Infrastructure;

namespace WebhookInspector.Api.Tests;

[Collection(PostgresCollection.Name)]
public class HealthTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_return_200(string path)
    {
        var response = await fixture.Api.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
