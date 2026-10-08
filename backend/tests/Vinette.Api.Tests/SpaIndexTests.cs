using Vinette.Api.Configuration;
using Vinette.Api.Spa;

namespace Vinette.Api.Tests;

public class SpaIndexTests
{
    private const string Html = "<html><head><title>Vinette</title><!--vinette-head--></head><body></body></html>";

    [Fact]
    public void Render_Injects_Title_Favicon_And_Config_Block()
    {
        var result = SpaIndex.Render(Html, new BrandingOptions { Name = "Acme <Mail>", Accent = "#112233" });

        Assert.Contains("<title>Acme &lt;Mail&gt;</title>", result);
        Assert.Contains("href=\"/favicon.svg\"", result);
        Assert.Contains("id=\"vinette-config\"", result);
        Assert.DoesNotContain("<Mail>", result);
        Assert.DoesNotContain(SpaIndex.HeadMarker, result);
    }

    [Fact]
    public void Render_Uses_Branding_Endpoints_When_Files_Configured()
    {
        var result = SpaIndex.Render(Html, new BrandingOptions { LogoPath = "/x/logo.png", FaviconPath = "/x/f.ico" });

        Assert.Contains("/branding/logo", result);
        Assert.Contains("href=\"/branding/favicon\"", result);
    }
}
