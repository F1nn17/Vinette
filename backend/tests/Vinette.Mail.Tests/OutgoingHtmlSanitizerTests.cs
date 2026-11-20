using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>
/// The cleaning of the HTML of a message that is sent (VIN-108): the editor's own text comes through as it was, and what a paste from Word or from a web
/// page brings along, or what a client that is not ours sends, does not. The attack corpus of the display sanitizer is run through it too.
/// </summary>
public sealed class OutgoingHtmlSanitizerTests
{
    private static readonly MessageHtmlSanitizer Sanitizer = new();

    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal)
    {
        "html", "head", "body", "a", "b", "blockquote", "br", "code", "div", "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "li", "ol", "p", "pre",
        "s", "span", "strike", "strong", "sub", "sup", "u", "ul",
    };

    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal) { "href", "rel", "start", "target" };

    private static string Clean(string html) => Sanitizer.SanitizeOutgoing(html).Html;

    [Theory]
    [InlineData("<p>One</p><p>Two</p>")]
    [InlineData("<p><strong>bold</strong> <em>italic</em> <u>under</u> <code>code</code></p>")]
    [InlineData("<ul><li><p>a</p></li><li><p>b</p></li></ul>")]
    [InlineData("<ol start=\"3\"><li><p>c</p></li></ol>")]
    [InlineData("<blockquote><p>quoted</p><blockquote><p>deeper</p></blockquote></blockquote>")]
    [InlineData("<p>a<br>b</p>")]
    [InlineData("<pre><code>x &lt; y</code></pre>")]
    [InlineData("<p>Привет, <strong>мир</strong>!</p>")]
    public void What_The_Editor_Makes_Comes_Through_As_It_Was(string html)
    {
        Assert.Equal(html, Clean(html));
    }

    [Fact]
    public void A_Link_To_The_Web_Or_To_An_Address_Stays_And_Is_Made_Safe_To_Follow()
    {
        var web = Clean("<p><a href=\"https://example.org/a?b=1&amp;c=2\">site</a></p>");
        var mail = Clean("<p><a href=\"mailto:anna@example.org\">anna</a></p>");

        Assert.Contains("href=\"https://example.org/a?b=1&amp;c=2\"", web, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", web, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", web, StringComparison.Ordinal);
        Assert.Contains("href=\"mailto:anna@example.org\"", mail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:x")]
    [InlineData("tel:+123456")]
    [InlineData("#inside")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.org/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/x")]
    public void A_Link_To_Anything_Else_Loses_Its_Address_But_Keeps_Its_Text(string href)
    {
        var clean = Clean($"<p><a href=\"{href}\">click</a></p>");

        Assert.DoesNotContain("href", clean, StringComparison.Ordinal);
        Assert.Contains("click", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void Styles_Classes_Ids_And_Foreign_Attributes_Are_Taken_Off_Whatever_Word_Or_A_Page_Put_On_Them()
    {
        var word = "<p class=\"MsoNormal\" style=\"margin:0cm;font-family:Calibri;color:#1F497D\" id=\"x\" lang=\"EN-US\" data-x=\"1\" onclick=\"alert(1)\">" +
                   "<span style=\"mso-bidi-font-weight:bold;font-size:11.0pt\">Text</span></p>";

        var clean = Clean(word);

        Assert.Equal("<p><span>Text</span></p>", clean);
    }

    [Fact]
    public void Pictures_Tables_Forms_Frames_And_Scripts_Do_Not_Leave_In_A_Message()
    {
        var clean = Clean(
            "<p>a</p><img src=\"https://tracker.example/p.gif\"><img src=\"data:image/png;base64,AAAA\"><table><tr><td>cell</td></tr></table>" +
            "<form action=\"https://evil.example\"><input name=x></form><iframe src=\"https://evil.example\"></iframe><script>alert(1)</script>" +
            "<style>p{background:url(https://evil.example/x)}</style><object data=\"x\"></object><svg onload=\"alert(1)\"></svg><p>b</p>");

        Assert.Equal("<p>a</p><p>b</p>", clean);
    }

    [Fact]
    public void A_Paste_From_Google_Docs_Keeps_Its_Text_And_Formatting_And_Loses_The_Wrapper_Styles()
    {
        var docs = "<b style=\"font-weight:normal;\" id=\"docs-internal-guid-1234\"><p dir=\"ltr\" style=\"line-height:1.38;margin-top:0pt\">" +
                   "<span style=\"font-size:11pt;font-family:Arial;color:#000000;font-weight:700\">Hello</span></p></b>";

        var clean = Clean(docs);

        Assert.DoesNotContain("style", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("docs-internal-guid", clean, StringComparison.Ordinal);
        Assert.Contains("Hello", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void It_Is_Stable_What_Comes_Out_Does_Not_Change_When_It_Is_Cleaned_Again()
    {
        var once = Clean("<p>a</p><a href=\"https://example.org\">x</a><div><script>1</script><b>y</b></div><p class=q style=\"x:y\">z</p>");

        Assert.Equal(once, Clean(once));
    }

    [Fact]
    public void Nothing_Is_Counted_As_Blocked_And_Blank_Input_Is_Empty()
    {
        Assert.Equal(SanitizedHtml.Empty, Sanitizer.SanitizeOutgoing(null));
        Assert.Equal(SanitizedHtml.Empty, Sanitizer.SanitizeOutgoing("  "));
        Assert.Equal(0, Sanitizer.SanitizeOutgoing("<img src=https://x.example/a.gif>").BlockedExternalResources);
    }

    [Fact]
    public void Markup_Nested_Beyond_Any_Real_Message_Is_Rejected_And_Too_Long_Markup_Is_Told_Apart()
    {
        var deep = string.Concat(Enumerable.Repeat("<div>", IMessageHtmlSanitizer.MaxNesting + 5)) + "x";
        var long_ = "<p>" + new string('a', IMessageHtmlSanitizer.MaxInputLength) + "</p>";

        Assert.True(Sanitizer.SanitizeOutgoing(deep).Rejected);
        var truncated = Sanitizer.SanitizeOutgoing(long_);
        Assert.True(truncated.Truncated);
        Assert.False(truncated.Rejected);
    }

    // The corpus of attacks of the display sanitizer (tests/fixtures/xss/vectors.json): none may come out alive from the sending side either.
    private sealed record Vector(string Id, string Html);

    private sealed record Corpus(Vector[] Vectors);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Corpus Load() =>
        JsonSerializer.Deserialize<Corpus>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "xss", "vectors.json")),
            Json)!;

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var vector in Load().Vectors)
        {
            data.Add(vector.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void No_Vector_Of_The_Corpus_Comes_Out_Alive_Or_Different_The_Second_Time(string id)
    {
        var vector = Load().Vectors.Single(v => v.Id == id);

        var result = Sanitizer.SanitizeOutgoing(vector.Html);

        Assert.False(result.Rejected);
        var document = new HtmlParser().ParseDocument($"<!DOCTYPE html><html><body>{result.Html}</body></html>");
        foreach (var element in document.All)
        {
            Assert.True(Tags.Contains(element.LocalName) && element.NamespaceUri == "http://www.w3.org/1999/xhtml", $"element <{element.LocalName}>");
            foreach (var attribute in element.Attributes)
            {
                Assert.True(Attributes.Contains(attribute.Name), $"attribute {attribute.Name} on <{element.LocalName}>");
                if (attribute.Name == "href")
                {
                    Assert.True(
                        element.LocalName == "a" && (attribute.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || attribute.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || attribute.Value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)),
                        $"link {attribute.Value}");
                }
            }

            if (element.LocalName == "a" && element.HasAttribute("href"))
            {
                Assert.Equal("_blank", element.GetAttribute("target"));
                Assert.Equal("noopener noreferrer", element.GetAttribute("rel"));
            }
        }

        Assert.Empty(document.Descendants<IComment>());
        Assert.Empty(document.Descendants<IProcessingInstruction>());
        Assert.Equal(result.Html, Sanitizer.SanitizeOutgoing(result.Html).Html); // mutation XSS: a second reading must not change it
    }
}
