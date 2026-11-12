using Vinette.Api.Mail;
using Vinette.Core.Mail;

namespace Vinette.Api.Tests;

public class InlineImageResolverTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static InlinePart Part(string id, byte[] content, string type = "image/png") =>
        new("1.2", id, type, content.Length, content);

    [Fact]
    public void A_Known_Picture_Becomes_An_Embedded_Address()
    {
        var resolver = new InlineImageResolver([Part("logo@example.org", Png)]);

        var uri = resolver.Resolve("logo@example.org");

        Assert.StartsWith("data:image/png;base64,", uri, StringComparison.Ordinal);
        Assert.Equal(1, resolver.Embedded);
    }

    [Theory]
    [InlineData("LOGO@EXAMPLE.ORG")]
    [InlineData("<logo@example.org>")]
    [InlineData("logo%40example.org")]
    [InlineData("  logo@example.org ")]
    public void The_Id_Is_Matched_Like_A_Browser_Would(string written)
    {
        Assert.NotNull(new InlineImageResolver([Part("logo@example.org", Png)]).Resolve(written));
    }

    [Fact]
    public void An_Unknown_Id_Gives_Nothing()
    {
        Assert.Null(new InlineImageResolver([Part("a@x", Png)]).Resolve("b@x"));
        Assert.Null(new InlineImageResolver([]).Resolve("a@x"));
    }

    [Fact]
    public void A_Part_That_Calls_Itself_A_Picture_But_Is_Not_Is_Not_Embedded()
    {
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg onload=\"alert(1)\"/>");
        var resolver = new InlineImageResolver([Part("a@x", html, "image/png"), Part("b@x", svg, "image/svg+xml"), Part("c@x", [], "image/png")]);

        Assert.Null(resolver.Resolve("a@x"));
        Assert.Null(resolver.Resolve("b@x"));
        Assert.Null(resolver.Resolve("c@x"));
        Assert.Equal(0, resolver.Embedded);
    }

    [Fact]
    public void A_Part_Whose_Declared_Type_Is_Wrong_But_Whose_Bytes_Are_A_Picture_Is_Embedded_With_The_Real_Type()
    {
        var resolver = new InlineImageResolver([Part("a@x", Png, "application/octet-stream")]);

        Assert.StartsWith("data:image/png;base64,", resolver.Resolve("a@x"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Part_Without_Its_Bytes_Because_It_Was_Too_Big_Is_Not_Embedded()
    {
        var big = new InlinePart("1.2", "big@x", "image/png", MessageLimits.MaxInlineImageBytes + 1, []);

        Assert.Null(new InlineImageResolver([big]).Resolve("big@x"));
    }

    [Fact]
    public void The_Same_Picture_Used_Twice_Costs_Its_Bytes_Once()
    {
        var resolver = new InlineImageResolver([Part("a@x", Png)]);

        var first = resolver.Resolve("a@x");
        var second = resolver.Resolve("A@X");

        Assert.Equal(first, second);
        Assert.Equal(1, resolver.Embedded);
    }

    [Fact]
    public void The_Pictures_After_The_Total_Limit_Are_Not_Embedded()
    {
        var image = new byte[MessageLimits.MaxInlineImageBytes];
        Png.CopyTo(image, 0);
        var parts = Enumerable.Range(0, 10).Select(i => Part($"i{i}@x", image)).ToList();
        var resolver = new InlineImageResolver(parts);

        var results = parts.Select(p => resolver.Resolve(p.ContentId)).ToList();

        Assert.Equal(MessageLimits.MaxInlineImagesTotalBytes / MessageLimits.MaxInlineImageBytes, results.Count(r => r is not null));
        Assert.Null(results[^1]);
    }

    [Fact]
    public void A_Broken_Percent_Escape_Does_Not_Throw()
    {
        Assert.Null(new InlineImageResolver([Part("a@x", Png)]).Resolve("%E0%A4%A"));
    }
}
