using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Mail;

namespace Vinette.Api.Tests;

public sealed class MetricsTests
{
    private const string Token = "a-long-enough-metrics-token-123";

    private static CancellationToken Token_ => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> Factory(bool enabled = true, string? token = Token) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Metrics:Enabled", enabled ? "true" : "false");
            if (token is not null)
            {
                b.UseSetting("Metrics:BearerToken", token);
            }
        });

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request, Token_);
    }

    [Fact]
    public async Task Metrics_Are_Off_By_Default_And_The_Path_Is_Not_Served()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics", Token_);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Without_A_Token_The_Endpoint_Answers_401()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await GetAsync(client, bearer: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Wrong_Token_Answers_401_And_Does_Not_Echo_It()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await GetAsync(client, "not-the-right-token-at-all");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Token_);
        Assert.DoesNotContain("not-the-right-token", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Session_Cookie_Is_Not_Enough()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Add("Cookie", "__Host-vinette_session=anything");

        var response = await client.SendAsync(request, Token_);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task With_The_Token_The_Endpoint_Serves_Prometheus_Text_Including_Mail_Metrics()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var gateway = (Vinette.Core.Mail.IMailGateway)factory.Services.GetService(typeof(Vinette.Core.Mail.IMailGateway))!;
        using var meterProbe = new MailMetrics(() => 3, () => 1); // a second meter with the same name: values appear once the exporter reads them
        meterProbe.ConnectionOpened();
        meterProbe.ConnectionClosed("idle");
        meterProbe.CommandFinished(TimeSpan.FromMilliseconds(20), "ok");
        Assert.NotNull(gateway);

        var response = await GetAsync(client, Token);
        var body = await response.Content.ReadAsStringAsync(Token_);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("vinette_mail_connections_open", body, StringComparison.Ordinal);
        Assert.Contains("vinette_mail_connections_closed_total", body, StringComparison.Ordinal);
        Assert.Contains("reason=\"idle\"", body, StringComparison.Ordinal);
        Assert.Contains("vinette_mail_command_duration_seconds_bucket", body, StringComparison.Ordinal);
        Assert.DoesNotContain("@", body, StringComparison.Ordinal); // no addresses in metric labels
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void Enabling_Metrics_Without_A_Usable_Token_Fails_Startup(string? token)
    {
        StartupFailure.AssertOptionsInvalid(b =>
        {
            b.UseSetting("Metrics:Enabled", "true");
            if (token is not null)
            {
                b.UseSetting("Metrics:BearerToken", token);
            }
        });
    }
}
