using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vinette.Api.Errors;

namespace Vinette.Api.Tests;

public sealed class ProblemDetailsTests
{
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(string environment = "Production")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddVinetteProblemDetails();

        var app = builder.Build();
        app.UseVinetteProblemDetails();
        app.MapGet("/boom", (Func<IResult>)(() => throw new InvalidOperationException("secret detail")));
        app.MapPost("/validate", () => Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Required"] }));
        app.MapGet("/ok", () => "ok");
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
    }

    [Fact]
    public async Task Unhandled_Exception_Becomes_500_Problem_Without_Internals()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/boom", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", problem.GetProperty("code").GetString());
        Assert.Equal("urn:vinette:error:internal_error", problem.GetProperty("type").GetString());
        Assert.Equal(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single(), problem.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("secret detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_Shows_Exception_Message_Only()
    {
        var (app, client) = await StartAsync("Development");
        await using var _ = app;

        var response = await client.GetAsync("/boom", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("secret detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_Error_Becomes_400_With_Field_Errors()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        var response = await client.PostAsync("/validate", null, TestContext.Current.CancellationToken);
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("errors", out var errors));
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        Assert.NotNull(problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Unknown_Route_Becomes_404_Problem()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/nope", TestContext.Current.CancellationToken);
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Valid_Incoming_Correlation_Id_Is_Echoed()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "abc-123_X.y");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("abc-123_X.y", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Theory]
    [InlineData("has spaces and <script>")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task Malformed_Incoming_Correlation_Id_Is_Replaced(string incoming)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ok");
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, incoming);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var id = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.NotEqual(incoming, id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Theory]
    [InlineData(400, "bad_request")]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "not_found")]
    [InlineData(429, "too_many_requests")]
    [InlineData(503, "service_unavailable")]
    [InlineData(500, "internal_error")]
    public void ErrorCodes_Map_Status(int status, string code)
    {
        Assert.Equal(code, ErrorCodes.ForStatus(status));
    }
}
