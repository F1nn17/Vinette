using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Endpoints;

namespace Vinette.Api.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_Returns_Ok_And_Version()
    {
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<HealthResponse>("/api/v1/health", TestContext.Current.CancellationToken);

        Assert.NotNull(body);
        Assert.Equal("ok", body.Status);
        Assert.Equal(HealthEndpoints.ProductVersion, body.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", body.Version);
    }

    [Fact]
    public async Task Unknown_Api_Route_Is_Not_Served_By_Spa_Fallback()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/nope", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
