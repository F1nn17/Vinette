using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vinette.Api.Auth;
using Vinette.Api.Sessions;
using Vinette.Core.Authentication;

namespace Vinette.Api.Tests;

public sealed class AuthTests : IDisposable
{
    private const string Address = "alice@vinette.test";
    private const string Password = "s3cret pass ";

    /// <summary>Scripted mail server: "alice@" signs in, "down@" is unavailable, everything else is rejected.</summary>
    private sealed class FakeAuthenticator : IAuthenticator
    {
        public List<(LoginRequest Request, ClientInfo Client)> Calls { get; } = [];

        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
        {
            Calls.Add((request, client));
            return Task.FromResult(request.Address switch
            {
                var a when a.StartsWith("alice@", StringComparison.Ordinal) && request.Password == Password => AuthenticationResult.Success,
                var a when a.StartsWith("down@", StringComparison.Ordinal) => AuthenticationResult.Unavailable("SocketException: secret-host:993 refused"),
                _ => AuthenticationResult.InvalidCredentials,
            });
        }
    }

    private readonly FakeAuthenticator authenticator = new();
    private readonly WebApplicationFactory<Program> factory;

    public AuthTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator>(authenticator);
            }));
    }

    public void Dispose() => factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HttpClient CreateClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string address, string password, bool rememberMe = false, string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { address, password, rememberMe }),
        };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request, Token);
    }

    private static string? SetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)) : null;

    private static string SessionIdFrom(string setCookie) =>
        setCookie[(SessionCookie.Name.Length + 1)..].Split(';')[0];

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;
    }

    // ---- Login ----

    [Fact]
    public async Task Login_Creates_A_Session_And_Sets_A_Hardened_Cookie()
    {
        var response = await LoginAsync(CreateClient(), $"  {Address}  ", Password);
        var body = await response.Content.ReadAsStringAsync(Token);
        var cookie = SetCookie(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Address, JsonDocument.Parse(body).RootElement.GetProperty("address").GetString());
        Assert.DoesNotContain("s3cret", body, StringComparison.Ordinal);

        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires", cookie, StringComparison.OrdinalIgnoreCase); // gone when the browser closes
        Assert.DoesNotContain("max-age", cookie, StringComparison.OrdinalIgnoreCase);

        var sessions = factory.Services.GetRequiredService<SessionService>();
        var session = await sessions.GetValidAsync(SessionIdFrom(cookie), Token);
        Assert.NotNull(session);
        Assert.Equal(Address, session.Address);
        Assert.Equal(Password, sessions.RevealPassword(session));
    }

    [Fact]
    public async Task Login_Passes_The_Trimmed_Address_And_The_Untouched_Password_To_The_Mail_Server()
    {
        await LoginAsync(CreateClient(), $"  {Address}  ", Password);

        var call = Assert.Single(authenticator.Calls);
        Assert.Equal(Address, call.Request.Address);
        Assert.Equal(Password, call.Request.Password);
    }

    [Fact]
    public async Task Remember_Me_Makes_The_Cookie_Persistent()
    {
        var response = await LoginAsync(CreateClient(), Address, Password, rememberMe: true);
        var cookie = SetCookie(response);

        Assert.NotNull(cookie);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_Password_Gives_401_Without_A_Cookie()
    {
        var response = await LoginAsync(CreateClient(), Address, "wrong");
        var problem = await ProblemAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", problem.GetProperty("code").GetString());
        Assert.Null(SetCookie(response));
    }

    [Fact]
    public async Task Unknown_User_Is_Indistinguishable_From_A_Wrong_Password()
    {
        var wrong = await LoginAsync(CreateClient(), Address, "wrong");
        var unknown = await LoginAsync(CreateClient(), "nobody@vinette.test", "wrong");

        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        Assert.Equal(
            (await ProblemAsync(wrong)).GetProperty("title").GetString(),
            (await ProblemAsync(unknown)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unavailable_Mail_Server_Gives_503_And_No_Internal_Details()
    {
        var response = await LoginAsync(CreateClient(), "down@vinette.test", "pw");
        var raw = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("mail_unavailable", JsonDocument.Parse(raw).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("secret-host", raw, StringComparison.Ordinal);
        Assert.Null(SetCookie(response));
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("alice@vinette.test", "")]
    public async Task Missing_Fields_Give_400_With_Field_Errors(string address, string password)
    {
        var response = await LoginAsync(CreateClient(), address, password);
        var problem = await ProblemAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("errors", out _));
        Assert.Empty(authenticator.Calls); // the mail server was not bothered
    }

    [Fact]
    public async Task Oversized_Input_Is_Rejected_Before_Reaching_The_Mail_Server()
    {
        var response = await LoginAsync(CreateClient(), new string('a', 300) + "@vinette.test", Password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(authenticator.Calls);
    }

    [Fact]
    public async Task Logging_In_Again_Replaces_The_Previous_Session()
    {
        var client = CreateClient();
        var first = SetCookie(await LoginAsync(client, Address, Password))!;
        var firstId = SessionIdFrom(first);

        var second = SetCookie(await LoginAsync(client, Address, Password, cookie: $"{SessionCookie.Name}={firstId}"))!;
        var secondId = SessionIdFrom(second);

        var sessions = factory.Services.GetRequiredService<SessionService>();
        Assert.NotEqual(firstId, secondId);
        Assert.Null(await sessions.GetValidAsync(firstId, Token));
        Assert.NotNull(await sessions.GetValidAsync(secondId, Token));
    }

    // ---- Logout ----

    [Fact]
    public async Task Logout_Ends_The_Session_And_Clears_The_Cookie()
    {
        var client = CreateClient();
        var id = SessionIdFrom(SetCookie(await LoginAsync(client, Address, Password))!);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={id}");

        var response = await client.SendAsync(request, Token);
        var cleared = SetCookie(response);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull(cleared);
        Assert.StartsWith($"{SessionCookie.Name}=;", cleared, StringComparison.Ordinal);
        Assert.Contains("secure", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await factory.Services.GetRequiredService<SessionService>().GetValidAsync(id, Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("0123456789012345678901234567890123456789012")]
    public async Task Logout_Without_A_Valid_Session_Is_Still_204(string? cookieValue)
    {
        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        if (cookieValue is not null)
        {
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={cookieValue}");
        }

        var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
