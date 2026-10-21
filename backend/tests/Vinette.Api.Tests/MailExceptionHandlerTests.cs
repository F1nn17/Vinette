using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Vinette.Api.Auth;
using Vinette.Api.Configuration;
using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;
using Vinette.Core.Sessions;

namespace Vinette.Api.Tests;

public sealed class MailExceptionHandlerTests
{
    private sealed class RecordingGateway : IMailGateway
    {
        public List<string> Released { get; } = [];

        public Task<TResult> ExecuteAsync<TResult>(
            MailCredentials credentials,
            Func<IMailSession, CancellationToken, Task<TResult>> command,
            CancellationToken cancellationToken,
            bool safeToRepeat = true) => throw new NotSupportedException();

        public Task ReleaseAsync(string sessionId, CancellationToken cancellationToken)
        {
            Released.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(WebApplication App, HttpClient Client, SessionService Sessions, RecordingGateway Gateway)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddVinetteProblemDetails();
        var gateway = new RecordingGateway();
        builder.Services.AddSingleton<IMailGateway>(gateway);
        builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
        builder.Services.AddSingleton<ICredentialProtector>(new DataProtectionCredentialProtector(new EphemeralDataProtectionProvider()));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(Options.Create(new SessionLifetimeOptions()));
        builder.Services.AddSingleton<SessionService>();

        var app = builder.Build();
        app.UseVinetteProblemDetails();
        app.MapGet("/revoked", (Func<IResult>)(() => throw new MailAccessRevokedException("password changed")));
        app.MapGet("/down", (Func<IResult>)(() => throw new MailUnavailableException("IOException: reset")));
        await app.StartAsync();
        return (app, app.GetTestClient(), app.Services.GetRequiredService<SessionService>(), gateway);
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;

    [Fact]
    public async Task A_Rejected_Saved_Password_Ends_The_Session_And_Answers_401()
    {
        var (app, client, sessions, gateway) = await StartAsync();
        await using var _ = app;
        var session = await sessions.CreateAsync("alice@vinette.test", "s3cret", false, null, Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/revoked");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={session.Id}");

        var response = await client.SendAsync(request, Token);
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("session_revoked", problem.GetProperty("code").GetString());
        Assert.Null(await sessions.GetValidAsync(session.Id, Token)); // gone on the server
        Assert.Equal([session.Id], gateway.Released); // and its mail connection closed
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith($"{SessionCookie.Name}=;", StringComparison.Ordinal)); // cookie erased
        Assert.DoesNotContain("s3cret", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Rejected_Password_Without_A_Session_Cookie_Is_Still_401()
    {
        var (app, client, _, gateway) = await StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/revoked", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(gateway.Released);
    }

    [Fact]
    public async Task An_Unreachable_Mail_Server_Is_A_Temporary_503_Without_Internals()
    {
        var (app, client, _, _) = await StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/down", Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("mail_unavailable", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("IOException", body, StringComparison.Ordinal);
    }
}
