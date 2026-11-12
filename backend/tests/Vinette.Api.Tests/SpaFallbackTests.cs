using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Vinette.Api.Tests;

/// <summary>Which addresses open the application: pages do, files that are not there and the API do not.</summary>
public sealed class SpaFallbackTests : IDisposable
{
    private readonly string webRoot = Path.Combine(Path.GetTempPath(), "vinette-spa-" + Guid.NewGuid().ToString("N"));

    private readonly WebApplicationFactory<Program> factory;

    public SpaFallbackTests()
    {
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html><head><title>x</title><!--vinette-head--></head><body><div id=\"root\"></div></body></html>");
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting(WebHostDefaults.WebRootKey, webRoot));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        factory.Dispose();
        Directory.Delete(webRoot, recursive: true);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/mail/inbox")]
    [InlineData("/mail/inbox?filter=unread")]
    [InlineData("/compose")]
    public async Task A_Page_Of_The_Application_Opens_It(string url)
    {
        var response = await factory.CreateClient().GetAsync(url, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("id=\"vinette-config\"", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/mail/inbox/SU5CT1g.1731000000.42")] // a message id has dots in it
    [InlineData("/mail/inbox/SU5CT1g.1731000000.42?filter=unread")]
    [InlineData("/mail/sent/U2VudA.1.1")]
    [InlineData("/mail/%D0%A0%D0%B0%D0%B1%D0%BE%D1%82%D0%B0/0KDQsNCx0L7RgtCw.7.9")] // a Cyrillic folder
    public async Task A_Link_To_A_Message_Opens_The_Application_After_A_Reload_Or_From_A_Bookmark(string url)
    {
        var response = await factory.CreateClient().GetAsync(url, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("id=\"vinette-config\"", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/assets/missing.js")]
    [InlineData("/missing.css")]
    [InlineData("/favicon.ico")]
    [InlineData("/some/other/file.map")]
    public async Task A_File_That_Is_Not_There_Is_Refused_Not_Answered_With_The_Application(string url)
    {
        var response = await factory.CreateClient().GetAsync(url, Token);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/v1/nothing")]
    [InlineData("/api/v1/messages/x.1.1/attachments/9.9.9.9.9")]
    [InlineData("/hubs/nothing")]
    [InlineData("/branding/nothing.1")]
    [InlineData("/metrics/nothing")]
    public async Task The_Api_And_Its_Neighbours_Never_Answer_With_The_Application(string url)
    {
        var response = await factory.CreateClient().GetAsync(url, Token);

        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_Application_Page_Is_Not_Cached_So_A_New_Version_Is_Seen_At_Once()
    {
        var response = await factory.CreateClient().GetAsync("/mail/inbox/SU5CT1g.1.1", Token);

        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }
}
