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
        app.MapGet("/busy", (Func<IResult>)(() => throw new MailBusyException("full", TimeSpan.FromSeconds(7))));
        app.MapGet("/down", (Func<IResult>)(() => throw new MailUnavailableException("IOException: reset")));
        app.MapGet("/message/{id}", (MessageId id) => id.ToString());
        app.MapGet("/message-gone", (Func<IResult>)(() => throw new MailMessageNotFoundException("uid 5 of Секретная папка")));
        app.MapGet("/message-large", (Func<IResult>)(() => throw new MailMessageTooLargeException(99_000_000)));
        app.MapGet("/folder-recreated", (Func<IResult>)(() =>
        {
            new MessageId("Секретная папка", 5, 1).EnsureCurrent(6);
            return Results.Ok();
        }));
        await app.StartAsync();
        return (app, app.GetTestClient(), app.Services.GetRequiredService<SessionService>(), gateway);
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response) =>
        JsonElement.Parse(await response.Content.ReadAsStringAsync(Token));

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
    public async Task Too_Many_Connections_Give_503_With_Retry_After()
    {
        var (app, client, _, _) = await StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/busy", Token);
        var problem = await ReadProblemAsync(response);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("mail_busy", problem.GetProperty("code").GetString());
        Assert.Equal("7", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task An_Unreachable_Mail_Server_Is_A_Temporary_503_Without_Internals()
    {
        var (app, client, _, _) = await StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/down", Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("mail_unavailable", JsonElement.Parse(body).GetProperty("code").GetString());
        Assert.DoesNotContain("IOException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Recreated_Folder_Gives_409_Without_The_Folder_Name()
    {
        var (app, client, _, _) = await StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/folder-recreated", Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("uid_validity_changed", JsonElement.Parse(body).GetProperty("code").GetString());
        Assert.DoesNotContain("Секретная", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Message_Id_In_The_Route_Is_Bound_And_A_Malformed_One_Is_A_400()
    {
        var (app, client, _, _) = await StartAsync();
        await using var _ = app;
        var id = new MessageId("INBOX", 1731000000, 42).ToString();

        var good = await client.GetAsync($"/message/{id}", Token);
        var bad = await client.GetAsync("/message/not-an-id", Token);

        Assert.Equal(id, await good.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_Message_That_Is_Gone_Gives_404_Without_Details()
    {
        var (app, client, _, _) = await StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/message-gone", Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", JsonElement.Parse(body).GetProperty("code").GetString());
        Assert.DoesNotContain("Секретная", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Message_That_Is_Too_Large_Gives_413_With_Its_Own_Code()
    {
        var (app, client, _, _) = await StartAsync();
        await using var _ = app;

        var response = await client.GetAsync("/message-large", Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", JsonElement.Parse(body).GetProperty("code").GetString());
        Assert.DoesNotContain("99000000", body, StringComparison.Ordinal);
    }
}
