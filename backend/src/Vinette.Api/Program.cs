using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Vinette.Api.Attachments;
using Vinette.Api.Auth;
using Vinette.Api.Configuration;
using Vinette.Api.Endpoints;
using Vinette.Api.Errors;
using Vinette.Api.Hubs;
using Vinette.Api.Mail;
using Vinette.Api.Scheduling;
using Vinette.Api.Security;
using Vinette.Api.Sessions;
using Vinette.Api.Settings;
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
builder.Services.AddVinetteMetrics(builder.Configuration);
builder.Services.AddVinetteSessions();
builder.Services.AddVinetteSettings();
builder.Services.AddVinetteAttachments();
builder.Services.AddVinetteScheduling();
builder.Services.AddVinetteAuthentication();
builder.Services.AddVinetteForwardedHeaders();
builder.Services.AddVinetteCsrf();
builder.Services.AddVinetteLoginRateLimiting();
builder.Services.AddVinetteProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer(async (document, context, cancellationToken) =>
{
    // The events of the real-time channel are not the answer of any endpoint, but the client needs their types: put them in the document.
    foreach (var type in new[] { typeof(ArrivedEventDto), typeof(RemovedEventDto), typeof(MarksEventDto), typeof(CountersEventDto), typeof(StaleEventDto) })
    {
        document.AddComponent(type.Name, await context.GetOrCreateSchemaAsync(type, null, cancellationToken));
    }
}));
builder.Services.AddSignalR();
builder.Services.AddSingleton<MailHubConnections>();

var keyStorage = builder.Configuration.GetSection(KeyStorageOptions.SectionName).Get<KeyStorageOptions>() ?? new KeyStorageOptions();
var keysPersisted = !string.IsNullOrWhiteSpace(keyStorage.KeysPath);
builder.Services.AddDataProtection().SetApplicationName("Vinette").UseVinetteKeys(keyStorage);

var app = builder.Build();

// The data directories are checked before anything listens: one that cannot be used stops the start with a message that names the setting.
try
{
    DataDirectories.EnsureWritable(DataDirectories.Configured(
        keyStorage,
        app.Services.GetRequiredService<IOptions<SettingsOptions>>().Value,
        app.Services.GetRequiredService<IOptions<AttachmentsOptions>>().Value,
        app.Services.GetRequiredService<IOptions<SchedulingOptions>>().Value)
        .Concat(DataDirectories.Temporary(Environment.GetEnvironmentVariable("TMPDIR"))));
}
catch (DataDirectories.DataDirectoryException exception)
{
    DataDirectories.LogProblem(app.Logger, exception.Message);
    return 1;
}

if (!keysPersisted)
{
    SessionServices.LogKeysNotPersisted(app.Logger);
}
else
{
    KeyProtectionSetup.LogStatus(app.Logger, keyStorage);
}

app.UseVinetteForwardedHeaders();
app.UseVinetteSecurityHeaders();
app.UseVinetteProblemDetails();
app.UseRateLimiter();
app.UseVinetteMetrics();

app.UseStaticFiles();
app.UseAuthentication();
app.UseVinetteCsrf();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

var api = app.MapGroup("/api/v1");
api.MapHealth();
api.MapAuth();
api.MapCsrf();
api.MapFolders();
api.MapMessageList();
api.MapMessages();
api.MapMessageActions();
api.MapLabels();
api.MapMessageBatch();
api.MapSettings();
api.MapAttachments();
api.MapDrafts();
api.MapSend();
api.MapReceipts();
api.MapScheduled();
api.MapQuote();
api.MapRecipientSuggestions();
app.MapHub<MailHub>("/hubs/mail");
app.MapBranding();
app.MapManifest();

app.MapSpa();

app.Run();
return 0;

/// <summary>Entry point marker for WebApplicationFactory.</summary>
public partial class Program;
