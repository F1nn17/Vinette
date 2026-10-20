using Microsoft.AspNetCore.DataProtection;
using Vinette.Api.Configuration;
using Vinette.Api.Endpoints;
using Vinette.Api.Errors;
using Vinette.Api.Hubs;
using Vinette.Api.Mail;
using Vinette.Api.Sessions;
using Vinette.Api.Spa;

// Container HEALTHCHECK: chiseled images ship no curl, so the app probes itself.
if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await http.GetAsync($"http://localhost:{port}/api/v1/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
    catch (TaskCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddVinetteConfiguration();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.Services.AddVinetteOptions(builder.Configuration);
builder.Services.AddVinetteMail();
builder.Services.AddVinetteSessions();
builder.Services.AddVinetteProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Vinette");
var keysPath = builder.Configuration[$"{KeyStorageOptions.SectionName}:{nameof(KeyStorageOptions.KeysPath)}"];
var keysPersisted = !string.IsNullOrWhiteSpace(keysPath);
if (keysPersisted)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath!));
}

var app = builder.Build();

if (!keysPersisted)
{
    SessionServices.LogKeysNotPersisted(app.Logger);
}

app.UseVinetteProblemDetails();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGroup("/api/v1").MapHealth();
app.MapHub<MailHub>("/hubs/mail");
app.MapBranding();

app.UseStaticFiles();
app.MapSpa();

app.Run();
return 0;

/// <summary>Entry point marker for WebApplicationFactory.</summary>
public partial class Program;
