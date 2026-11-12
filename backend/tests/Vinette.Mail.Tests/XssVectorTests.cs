using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>
/// The sanitizer against a corpus of known attack shapes (tests/fixtures/xss/vectors.json, format in its README). The
/// corpus is data: a new vector is a new object in the file, no code changes. Every output is judged by an oracle that
/// parses it again like a browser would and looks at the resulting tree, not at the text the sanitizer wrote.
/// </summary>
public partial class XssVectorTests
{
    private sealed record Vector(string Id, string Category, string Kind, string Html, string[]? Keeps, int? MinBlocked, string? Note);

    private sealed record Corpus(Vector[] Vectors);

    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "xss");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly MessageHtmlSanitizer Sanitizer = new();

    // The oracle's own notion of what may be in the output: it must not be looser than the documented white list.
    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "html", "head", "body",
        "a", "abbr", "acronym", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote", "br", "caption", "center",
        "cite", "code", "col", "colgroup", "dd", "del", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font",
        "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "i", "img", "ins", "kbd", "li", "main", "mark", "nav",
        "ol", "p", "pre", "q", "s", "samp", "section", "small", "span", "strike", "strong", "style", "sub", "sup", "table",
        "tbody", "td", "tfoot", "th", "thead", "tr", "tt", "u", "ul", "var", "wbr",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "class", "color", "colspan", "dir", "face", "height",
        "href", "hspace", "id", "lang", "nowrap", "rowspan", "size", "src", "start", "style", "title", "valign", "vspace",
        "width", "span", "type", "target", "rel",
    };

    [GeneratedRegex(@"url\s*\(|image-set\s*\(|expression\s*\(|javascript\s*:|vbscript\s*:|behavior\s*:|-moz-binding|@import|@font-face|@namespace|\\|position\s*:|z-index", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveCss();

    [GeneratedRegex(@"^data:image/(png|jpeg|gif|webp);base64,[A-Za-z0-9+/=\s]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SafeDataImage();

    private static Corpus Load() =>
        JsonSerializer.Deserialize<Corpus>(File.ReadAllText(Path.Combine(Dir, "vectors.json")), JsonOptions)!;

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var vector in Load().Vectors)
        {
            data.Add(vector.Id);
        }

        return data;
    }

    /// <summary>Every way the output could still do something; an empty list means the output is inert.</summary>
    private static List<string> Violations(string output)
    {
        var problems = new List<string>();
        var document = new HtmlParser().ParseDocument($"<!DOCTYPE html><html><body>{output}</body></html>");

        foreach (var element in document.All)
        {
            var tag = element.LocalName;
            if (!AllowedTags.Contains(tag) || element.NamespaceUri != "http://www.w3.org/1999/xhtml")
            {
                problems.Add($"element <{tag}> ({element.NamespaceUri})");
            }

            foreach (var attribute in element.Attributes)
            {
                var name = attribute.Name;
                var value = attribute.Value;
                if (!AllowedAttributes.Contains(name))
                {
                    problems.Add($"attribute {name} on <{tag}>");
                    continue;
                }

                switch (name)
                {
                    case "href" when tag == "a":
                        if (!(value.StartsWith('#') || IsScheme(value, "https:") || IsScheme(value, "http:") || IsScheme(value, "mailto:") || IsScheme(value, "tel:")))
                        {
                            problems.Add($"link {value}");
                        }

                        break;
                    case "href":
                        problems.Add($"href on <{tag}>");
                        break;
                    case "src" when tag == "img":
                        if (!(IsScheme(value, "cid:") && value.Length > 4) && !SafeDataImage().IsMatch(value))
                        {
                            problems.Add($"image source {Shorten(value)}");
                        }

                        break;
                    case "src":
                        problems.Add($"src on <{tag}>");
                        break;
                    case "style" when ActiveCss().IsMatch(value):
                        problems.Add($"style {value}");
                        break;
                    case "target" when value != "_blank":
                        problems.Add($"target {value}");
                        break;
                    case "rel" when value != "noopener noreferrer":
                        problems.Add($"rel {value}");
                        break;
                }
            }

            if (tag == "style" && ActiveCss().IsMatch(element.TextContent))
            {
                problems.Add($"style element {Shorten(element.TextContent)}");
            }

            if (tag == "a" && element.HasAttribute("href") && (element.GetAttribute("target") != "_blank" || element.GetAttribute("rel") != "noopener noreferrer"))
            {
                problems.Add("link without target and rel");
            }
        }

        if (document.Descendants<IComment>().Any())
        {
            problems.Add("comment");
        }

        if (document.Descendants<IProcessingInstruction>().Any())
        {
            problems.Add("processing instruction");
        }

        return problems;
    }

    private static bool IsScheme(string value, string scheme) => value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string value) => value.Length <= 80 ? value : value[..80] + "…";

    [Theory]
    [MemberData(nameof(Ids))]
    public void The_Vector_Comes_Out_Inert_Stable_And_As_Described(string id)
    {
        var vector = Load().Vectors.Single(v => v.Id == id);

        var result = Sanitizer.Sanitize(vector.Html);

        Assert.Empty(Violations(result.Html));
        Assert.False(result.Rejected);

        // Mutation XSS: what comes out must not change when it is read and written again, or a second parse could turn
        // text into markup. The sanitizer's own output is the input of the second run.
        var again = Sanitizer.Sanitize(result.Html);
        Assert.Equal(result.Html, again.Html);

        foreach (var expected in vector.Keeps ?? [])
        {
            Assert.Contains(expected, result.Html, StringComparison.Ordinal);
        }

        if (vector.MinBlocked is { } minimum)
        {
            Assert.True(result.BlockedExternalResources >= minimum, $"Counted {result.BlockedExternalResources}, expected at least {minimum}.");
        }

        if (vector.Kind == "benign")
        {
            Assert.Equal(0, result.BlockedExternalResources);
        }
    }

    [Fact]
    public void The_Corpus_Is_Well_Formed_And_Covers_Every_Family_Of_Attack()
    {
        var vectors = Load().Vectors;

        Assert.Equal(vectors.Length, vectors.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(vectors, v => Assert.False(string.IsNullOrWhiteSpace(v.Html), v.Id));
        Assert.All(vectors, v => Assert.StartsWith(v.Category + "-", v.Id, StringComparison.Ordinal));
        Assert.All(vectors, v => Assert.True(v.Kind is "attack" or "benign", v.Id));

        var categories = vectors.Select(v => v.Category).Distinct().ToHashSet();
        foreach (var required in new[] { "script", "event", "url", "encoding", "svg", "css", "data", "mxss", "active", "attribute", "external", "benign" })
        {
            Assert.Contains(required, categories);
        }

        Assert.True(vectors.Length >= 300, $"Only {vectors.Length} vectors.");
    }

    [Fact]
    public void The_Oracle_Itself_Catches_What_It_Is_Meant_To_Catch()
    {
        // A check that cannot fail proves nothing: these are the shapes of output the oracle must flag.
        foreach (var bad in new[]
        {
            "<script>alert(1)</script>",
            "<img src=\"x\" onerror=\"alert(1)\">",
            "<a href=\"javascript:alert(1)\" target=\"_blank\" rel=\"noopener noreferrer\">x</a>",
            "<a href=\"https://example.org\">no target</a>",
            "<img src=\"http://evil.example/x.png\">",
            "<img src=\"data:image/svg+xml;base64,AAAA\">",
            "<div style=\"background:url(http://evil.example/x)\">x</div>",
            "<style>p{background:url(x)}</style>",
            "<svg><circle></circle></svg>",
            "<div data-x=\"1\">x</div>",
            "<!-- comment -->",
            "<iframe src=\"cid:x\"></iframe>",
            "<div style=\"position:fixed\">x</div>",
        })
        {
            Assert.NotEmpty(Violations(bad));
        }

        Assert.Empty(Violations("<p style=\"color: red\">fine <a href=\"https://example.org\" target=\"_blank\" rel=\"noopener noreferrer\">link</a> <img src=\"cid:a\"></p>"));
    }
}
