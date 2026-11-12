using System.Diagnostics;
using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public class PlainTextHtmlTests
{
    private static string Html(string text) => PlainTextHtml.ToHtml(text, out _);

    [Fact]
    public void Nothing_Gives_Nothing()
    {
        Assert.Equal(string.Empty, Html(string.Empty));
        Assert.Equal(string.Empty, PlainTextHtml.ToHtml(null, out var truncated));
        Assert.False(truncated);
    }

    [Fact]
    public void The_Text_Is_Escaped_So_That_Nothing_In_It_Is_Markup()
    {
        var html = Html("<script>alert(\"x\")</script> & <b>bold</b> 'q'");

        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; &lt;b&gt;bold&lt;/b&gt; &#39;q&#39;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_Breaks_And_Spaces_Are_Kept()
    {
        var html = Html("one\r\ntwo\rthree\n\n  indented");

        Assert.Contains("one\ntwo\nthree\n\n  indented", html, StringComparison.Ordinal);
        Assert.StartsWith("<div style=\"white-space:pre-wrap", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("see https://example.org/a?b=1&c=2 now", "href=\"https://example.org/a?b=1&amp;c=2\"", ">https://example.org/a?b=1&amp;c=2</a>")]
    [InlineData("see http://example.org now", "href=\"http://example.org\"", ">http://example.org</a>")]
    [InlineData("see www.example.org now", "href=\"https://www.example.org\"", ">www.example.org</a>")]
    [InlineData("mail anna@example.org please", "href=\"mailto:anna@example.org\"", ">anna@example.org</a>")]
    [InlineData("mail anna.smirnova+work@mail.example.org.", "href=\"mailto:anna.smirnova+work@mail.example.org\"", ">anna.smirnova+work@mail.example.org</a>")]
    public void Addresses_Become_Links(string text, string href, string label)
    {
        var html = Html(text);

        Assert.Contains(href, html, StringComparison.Ordinal);
        Assert.Contains(label, html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Go to https://example.org/page.", "href=\"https://example.org/page\"")]
    [InlineData("Is it https://example.org/page?", "href=\"https://example.org/page\"")]
    [InlineData("(see https://example.org/page)", "href=\"https://example.org/page\"")]
    [InlineData("<https://example.org/page>", "href=\"https://example.org/page\"")]
    [InlineData("\"https://example.org/page\"", "href=\"https://example.org/page\"")]
    [InlineData("https://en.wikipedia.org/wiki/Mail_(disambiguation)", "href=\"https://en.wikipedia.org/wiki/Mail_(disambiguation)\"")]
    [InlineData("a, https://example.org/page, b", "href=\"https://example.org/page\"")]
    public void The_Punctuation_Around_An_Address_Is_Not_Part_Of_It(string text, string href)
    {
        Assert.Contains(href, Html(text), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://example.org/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://")]
    [InlineData("http:// not a link")]
    [InlineData("example@")]
    [InlineData("a@b")]
    [InlineData("@handle and user@localhost")]
    public void Anything_That_Is_Not_A_Web_Or_Mail_Address_Stays_Plain_Text(string text)
    {
        Assert.DoesNotContain("<a ", Html(text), StringComparison.Ordinal);
    }

    [Fact]
    public void An_Address_With_Markup_Characters_Cannot_Break_Out_Of_The_Link()
    {
        var html = Html("https://example.org/\"onmouseover=\"alert(1) and https://example.org/<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("onmouseover=\"alert", html.Replace("&quot;", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"onmouseover", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_Addresses_On_One_Line_Are_Two_Links()
    {
        var html = Html("https://a.example/x https://b.example/y anna@example.org");

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(html, "<a href="));
    }

    [Fact]
    public void Quoted_Lines_Become_Nested_Quotations_And_The_Marker_Is_Not_Shown()
    {
        var html = Html("Anna wrote:\n> first\n> second\n>> deeper\n> back\nmy reply");

        Assert.Contains("<blockquote>first\nsecond<blockquote>deeper</blockquote>back</blockquote>my reply", html, StringComparison.Ordinal);
        Assert.DoesNotContain("&gt;", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("> one", 1)]
    [InlineData(">> two", 2)]
    [InlineData("> > two", 2)]
    [InlineData(" >  > two with spaces", 2)]
    public void The_Depth_Of_A_Quotation_Is_Counted_Whatever_The_Spacing(string line, int depth)
    {
        Assert.Equal(depth, System.Text.RegularExpressions.Regex.Count(Html(line), "<blockquote>"));
    }

    [Fact]
    public void A_Greater_Than_Sign_In_The_Middle_Of_A_Line_Is_Just_Text()
    {
        var html = Html("1 > 0 and a -> b");

        Assert.DoesNotContain("<blockquote>", html, StringComparison.Ordinal);
        Assert.Contains("1 &gt; 0 and a -&gt; b", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotations_Nested_Absurdly_Deep_Are_Limited()
    {
        var html = Html(new string('>', 500) + " text");

        Assert.True(System.Text.RegularExpressions.Regex.Count(html, "<blockquote>") <= 8);
        Assert.Contains("text", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Text_Longer_Than_The_Limit_Is_Cut_And_Says_So()
    {
        var html = PlainTextHtml.ToHtml(new string('a', PlainTextHtml.MaxLength + 10), out var truncated);

        Assert.True(truncated);
        Assert.True(html.Length < PlainTextHtml.MaxLength + 200);
    }

    [Fact]
    public void Hostile_Input_Is_Handled_In_Linear_Time()
    {
        // Many things that look like the beginning of an address, none of which is one.
        var inputs = new[]
        {
            string.Concat(Enumerable.Repeat("http://", 20_000)),
            string.Concat(Enumerable.Repeat("a@", 50_000)),
            new string('a', 200_000) + "@",
            string.Concat(Enumerable.Repeat("www.", 50_000)),
            string.Concat(Enumerable.Repeat("(", 100_000)) + "https://a.example/",
            "https://a.example/" + string.Concat(Enumerable.Repeat(")", 100_000)),
            string.Join("\n", Enumerable.Repeat("> ", 50_000)),
        };

        var watch = Stopwatch.StartNew();
        foreach (var input in inputs)
        {
            _ = PlainTextHtml.ToHtml(input, out _);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void Cyrillic_And_Other_Scripts_Pass_Through()
    {
        Assert.Contains("Привет, мир! 日本語", Html("Привет, мир! 日本語"), StringComparison.Ordinal);
    }
}
