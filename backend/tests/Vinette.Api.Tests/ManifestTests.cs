using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Configuration;
using Vinette.Api.Endpoints;

namespace Vinette.Api.Tests;

public sealed class ManifestTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static JsonElement Parse(string text) => JsonElement.Parse(text);

    [Fact]
    public void The_Manifest_Carries_The_Name_The_Accent_And_The_Built_In_Icons_By_Default()
    {
        var manifest = Parse(ManifestEndpoint.Render(new BrandingOptions()));

        Assert.Equal("Vinette", manifest.GetProperty("name").GetString());
        Assert.Equal("Vinette", manifest.GetProperty("short_name").GetString());
        Assert.Equal("#8E2043", manifest.GetProperty("theme_color").GetString());
        Assert.Equal("standalone", manifest.GetProperty("display").GetString());
        Assert.Equal("/", manifest.GetProperty("start_url").GetString());
        Assert.Equal(
            ["/icons/icon-192.png", "/icons/icon-512.png"],
            manifest.GetProperty("icons").EnumerateArray().Select(i => i.GetProperty("src").GetString()).ToArray());
    }

    [Fact]
    public void A_Long_Name_Gets_A_Short_Name_That_Fits_Under_An_Icon()
    {
        var manifest = Parse(ManifestEndpoint.Render(new BrandingOptions { Name = "Acme Corporation Mail" }));

        Assert.Equal("Acme Corporation Mail", manifest.GetProperty("name").GetString());
        Assert.Equal("Acme Corpora", manifest.GetProperty("short_name").GetString());
    }

    [Fact]
    public void The_Name_Is_Text_Whatever_Characters_It_Has()
    {
        var manifest = Parse(ManifestEndpoint.Render(new BrandingOptions { Name = "Почта \"Рога\" <b>" }));

        Assert.Equal("Почта \"Рога\" <b>", manifest.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("/x/logo.svg", "image/svg+xml")]
    [InlineData("/x/logo.png", "image/png")]
    public void A_Logo_Of_The_Administrator_Is_The_Icon(string path, string type)
    {
        var manifest = Parse(ManifestEndpoint.Render(new BrandingOptions { LogoPath = path, Accent = "#112233" }));

        var icon = Assert.Single(manifest.GetProperty("icons").EnumerateArray());
        Assert.Equal("/branding/logo", icon.GetProperty("src").GetString());
        Assert.Equal(type, icon.GetProperty("type").GetString());
        Assert.Equal("any", icon.GetProperty("sizes").GetString());
        Assert.Equal("#112233", manifest.GetProperty("theme_color").GetString());
    }

    [Fact]
    public async Task The_Endpoint_Answers_Without_Signing_In_With_The_Configured_Name()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Branding:Name", "Acme Mail");
            b.UseSetting("Branding:Accent", "#0A5C36");
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/manifest.webmanifest", Token);
        var manifest = Parse(await response.Content.ReadAsStringAsync(Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Acme Mail", manifest.GetProperty("name").GetString());
        Assert.Equal("#0A5C36", manifest.GetProperty("theme_color").GetString());
    }
}
