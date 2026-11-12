using System.Diagnostics;
using System.Text;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>
/// Markup built to make a cleaner work for a very long time or use a very large amount of memory ("HTML bombs"). Each one
/// has to be cleaned (or refused) quickly: the time limit is the test.
/// </summary>
public class HtmlBombTests
{
    private static readonly MessageHtmlSanitizer Sanitizer = new();

    /// <summary>Generous for a slow test machine; a real mail takes a few milliseconds.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static string Repeat(string unit, int times) => new StringBuilder(unit.Length * times).Insert(0, unit, times).ToString();

    public static TheoryData<string> Bombs() => new()
    {
        "deep-divs",
        "deep-tables-under-the-limit",
        "many-siblings",
        "one-huge-attribute",
        "many-attributes",
        "many-styles",
        "misnested-formatting",
        "adoption-agency",
        "entities",
        "comments",
        "links",
        "huge-css-selector-list",
        "unclosed-tags",
        "nested-quotes-in-attributes",
        "table-cells",
        "lists",
        "empty-input-with-whitespace",
    };

    private static string Build(string name) => name switch
    {
        "deep-divs" => Repeat("<div>", 150_000) + "x",
        "deep-tables-under-the-limit" => Repeat("<table><tr><td>", 60) + "x" + Repeat("</td></tr></table>", 60),
        "many-siblings" => Repeat("<span>a</span>", 60_000),
        "one-huge-attribute" => "<p title=\"" + new string('a', 240_000) + "\">x</p>",
        "many-attributes" => "<p " + Repeat("a=1 ", 30_000) + ">x</p>",
        "many-styles" => Repeat("<style>p{color:red}</style>", 8_000),
        "misnested-formatting" => Repeat("<b><i>", 30_000) + "x",
        "adoption-agency" => Repeat("<a href=\"#\"><table><a>", 4_000) + "x",
        "entities" => Repeat("&amp;&lt;&gt;&#x41;&#65;", 30_000),
        "comments" => Repeat("<!--x-->", 30_000) + "y",
        "links" => Repeat("<a href=\"https://example.org/x\">l</a>", 8_000),
        "huge-css-selector-list" => "<style>" + string.Join(",", Enumerable.Range(0, 20_000).Select(i => $".c{i}")) + "{color:red}</style><p>x</p>",
        "unclosed-tags" => Repeat("<p>", 60_000) + Repeat("<td>", 60_000),
        "nested-quotes-in-attributes" => Repeat("<a title='\"'>", 20_000),
        "table-cells" => "<table><tr>" + Repeat("<td>c</td>", 40_000) + "</tr></table>",
        "lists" => Repeat("<ul><li>", 90) + "x" + Repeat("</li></ul>", 90) + Repeat("<li>i</li>", 30_000),
        "empty-input-with-whitespace" => new string(' ', 240_000),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Bombs))]
    public void Every_Bomb_Is_Cleaned_Or_Refused_Within_The_Time_Limit_And_Leaves_Nothing_Active(string name)
    {
        var html = Build(name);
        var watch = Stopwatch.StartNew();

        var result = Sanitizer.Sanitize(html, null, MessageLimits.DefaultBodyChars);

        Assert.True(watch.Elapsed < Limit, $"{name}: took {watch.Elapsed} for {html.Length} characters");
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Html.Length < 40_000_000, $"{name}: the output is {result.Html.Length} characters");
    }

    [Fact]
    public void Markup_Nested_Deeper_Than_Any_Mail_Is_Refused_Not_Cleaned()
    {
        var result = Sanitizer.Sanitize(Build("deep-divs"), null, MessageLimits.DefaultBodyChars);

        Assert.True(result.Rejected);
    }

    [Fact]
    public void The_Length_Asked_For_Is_The_Length_That_Is_Cleaned()
    {
        var html = Repeat("<p>word</p>", 100_000); // 1.1 million characters

        var short_ = Sanitizer.Sanitize(html, null, 1_000);
        var full = Sanitizer.Sanitize(html, null, MessageLimits.FullBodyChars);

        Assert.True(short_.Truncated);
        Assert.True(short_.Html.Length < 2_000);
        Assert.True(full.Truncated); // 1.1 million is over the most that is ever shown
        Assert.True(full.Html.Length > short_.Html.Length * 100);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public void A_Silly_Length_Is_Brought_Into_Range(int asked)
    {
        var result = Sanitizer.Sanitize("<p>x</p>" + Repeat("y", 1_200_000), null, asked);

        Assert.True(result.Truncated);
        Assert.True(result.Html.Length <= MessageLimits.FullBodyChars + 100);
    }

    [Fact]
    public void A_Body_That_Fits_Is_Not_Marked_As_Cut()
    {
        var result = Sanitizer.Sanitize("<p>short</p>", null, MessageLimits.DefaultBodyChars);

        Assert.False(result.Truncated);
        Assert.Contains("short", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Body_Cut_In_The_Middle_Of_A_Tag_Still_Gives_Clean_Markup()
    {
        var html = "<p>start</p><a href=\"https://example.org\" onclick=\"alert(1)\" title=\"" + new string('a', 5_000) + "\">link</a>";

        var result = Sanitizer.Sanitize(html, null, 60);

        Assert.True(result.Truncated);
        Assert.DoesNotContain("onclick", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_Bombs_One_After_Another_Do_Not_Add_Up_To_A_Hang()
    {
        var watch = Stopwatch.StartNew();

        foreach (var name in new[] { "many-siblings", "links", "table-cells", "entities" })
        {
            _ = Sanitizer.Sanitize(Build(name), null, MessageLimits.DefaultBodyChars);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Took {watch.Elapsed}");
    }
}
