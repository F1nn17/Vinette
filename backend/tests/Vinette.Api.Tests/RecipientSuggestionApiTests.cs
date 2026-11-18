using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vinette.Api.Auth;
using Vinette.Core.Authentication;
using Vinette.Core.Mail;

namespace Vinette.Api.Tests;

/// <summary>The suggestions endpoint through the real application (VIN-095), with a stand-in for the suggestions: what is asked and what comes back.</summary>
public sealed class RecipientSuggestionApiTests : IDisposable
{
    private const string Password = "pw";

    private sealed class AnyoneAuthenticator : IAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken) =>
            Task.FromResult(request.Password == Password ? AuthenticationResult.Success : AuthenticationResult.InvalidCredentials);
    }

    private sealed class FakeSuggestions : IRecipientSuggestions
    {
        public List<(string Mailbox, string Query, int Count)> Asked { get; } = [];

        public Exception? Fail { get; set; }

        public Task<IReadOnlyList<MailAddressInfo>> SuggestAsync(MailCredentials credentials, string query, int count, CancellationToken cancellationToken)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Asked.Add((credentials.Address, query, count));
            return Task.FromResult<IReadOnlyList<MailAddressInfo>>([new MailAddressInfo("Анна", "anna@example.org"), new MailAddressInfo("", "boris@example.org")]);
        }

        public void Remember(string mailbox, IEnumerable<MailAddressInfo> recipients)
        {
        }
    }

    private readonly FakeSuggestions suggestions = new();
    private readonly WebApplicationFactory<Program> factory;

    public RecipientSuggestionApiTests() =>
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator>(new AnyoneAuthenticator());
                services.RemoveAll<IRecipientSuggestions>();
                services.AddSingleton<IRecipientSuggestions>(suggestions);
            }));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factory.Dispose();

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url, bool signedIn = true)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (signedIn)
        {
            var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address = "alice@vinette.test", password = Password, rememberMe = false }, Token);
            var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={cookie.Split(';')[0][(SessionCookie.Name.Length + 1)..]}");
        }

        using var response = await http.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    [Fact]
    public async Task Suggestions_Need_A_Session()
    {
        var (status, _) = await GetAsync("/api/v1/recipients/suggestions?q=an", signedIn: false);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task What_Was_Typed_Is_Passed_On_And_The_People_Come_Back_In_Order()
    {
        var (status, body) = await GetAsync("/api/v1/recipients/suggestions?q=%D0%B0%D0%BD&limit=5");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("alice@vinette.test", "ан", 5), Assert.Single(suggestions.Asked));
        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("anna@example.org", items[0].GetProperty("address").GetString());
        Assert.Equal("Анна", items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Without_Parameters_The_Default_Count_And_An_Empty_Query_Are_Used()
    {
        var (status, _) = await GetAsync("/api/v1/recipients/suggestions");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("alice@vinette.test", string.Empty, SuggestionLimits.DefaultCount), Assert.Single(suggestions.Asked));
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=21")]
    [InlineData("limit=-3")]
    public async Task A_Count_Outside_The_Range_Is_A_Bad_Request(string query)
    {
        var (status, body) = await GetAsync("/api/v1/recipients/suggestions?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", body.GetProperty("code").GetString());
        Assert.Empty(suggestions.Asked);
    }

    [Fact]
    public async Task A_Query_That_Is_Too_Long_Is_A_Bad_Request()
    {
        var (status, _) = await GetAsync("/api/v1/recipients/suggestions?q=" + new string('a', SuggestionLimits.MaxQueryLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(suggestions.Asked);
    }

    [Fact]
    public async Task A_Mail_Server_That_Is_Down_Is_Reported_As_Such()
    {
        suggestions.Fail = new MailUnavailableException("down");

        var (status, body) = await GetAsync("/api/v1/recipients/suggestions?q=a");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("mail_unavailable", body.GetProperty("code").GetString());
    }
}
