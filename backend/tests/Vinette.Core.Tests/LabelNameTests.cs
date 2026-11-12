using Vinette.Core.Mail;
using Vinette.Core.Settings;

namespace Vinette.Core.Tests;

/// <summary>How the name of a label is kept as an IMAP keyword.</summary>
public sealed class LabelNameTests
{
    private static readonly string[] Different = ["Работа", "Работа ", "Работы", "работа"];

    [Theory]
    [InlineData("Work")]
    [InlineData("Bills-2026")]
    [InlineData("a")]
    [InlineData("under_score.dot")]
    public void A_Simple_Name_Is_The_Keyword_Itself(string name)
    {
        Assert.Equal(name, LabelName.ToKeyword(name));
        Assert.Equal(name, LabelName.FromKeyword(name));
    }

    [Theory]
    [InlineData("Работа")]
    [InlineData("Счета 2026")]
    [InlineData("two words")]
    [InlineData("2026")] // does not start with a letter
    [InlineData("Café")]
    [InlineData("vl-looks-like-ours")] // would be taken for one of ours
    [InlineData("日本語")]
    [InlineData("a,b")] // a character a keyword cannot hold
    public void Any_Other_Name_Is_Coded_And_Read_Back_As_It_Was(string name)
    {
        var keyword = LabelName.ToKeyword(name);

        Assert.StartsWith("vl-", keyword, StringComparison.Ordinal);
        Assert.Matches("^[a-z0-9-]+$", keyword); // letters of one case and digits only: the server's case folding cannot change it
        Assert.Equal(name, LabelName.FromKeyword(keyword));
    }

    [Fact]
    public void A_Keyword_Is_Read_Back_Even_If_The_Server_Changes_Its_Case()
    {
        var keyword = LabelName.ToKeyword("Счета 2026");

        Assert.Equal("Счета 2026", LabelName.FromKeyword(keyword.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("vl-")]
    [InlineData("vl-!!!")]
    [InlineData("vl-a")] // decodes to a byte that is not text
    [InlineData("Other")]
    [InlineData("vl-77777777")]
    public void A_Keyword_That_Is_Not_Ours_Is_Its_Own_Name(string keyword)
    {
        var name = LabelName.FromKeyword(keyword);

        Assert.NotNull(name);
        Assert.NotEmpty(name);
    }

    [Fact]
    public void Coded_Names_Differ_For_Different_Names()
    {
        var keywords = Different.Select(LabelName.ToKeyword).ToList();

        Assert.Equal(keywords.Count - 1, keywords.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // "Работа " is the same name: the spaces around are dropped
    }

    [Theory]
    [InlineData("Work", true)]
    [InlineData("  Work  ", true)]
    [InlineData("Счета 2026", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("tab\there", false)]
    [InlineData("new\nline", false)]
    public void A_Name_Is_Text_Without_Slashes_Or_Control_Characters(string name, bool valid)
    {
        Assert.Equal(valid, LabelName.IsValid(name, out var problem));
        Assert.Equal(valid, problem is null);
    }

    [Fact]
    public void The_Server_Limit_On_A_Keyword_Decides_How_Long_A_Name_Can_Be()
    {
        Assert.True(LabelName.IsValid(new string('a', LabelName.MaxKeywordLength), out _)); // simple: the keyword is the name
        Assert.False(LabelName.IsValid(new string('a', LabelName.MaxKeywordLength + 1), out _));
        Assert.True(LabelName.IsValid(new string('я', 14), out _)); // coded: about 1.6 characters per byte
        Assert.False(LabelName.IsValid(new string('я', 15), out _));
        Assert.True(LabelName.ToKeyword(new string('я', 14)).Length <= LabelName.MaxKeywordLength);
    }

    [Theory]
    [InlineData("work", "WORK", true)]
    [InlineData(" Work ", "work", true)]
    [InlineData("Работа", "работа", true)]
    [InlineData("Work", "Bills", false)]
    public void Names_Are_The_Same_Label_Without_Regard_To_Case(string a, string b, bool same) =>
        Assert.Equal(same, LabelName.Same(a, b));

    [Fact]
    public void Settings_Keep_A_Label_Once_Sorted_With_Its_Color()
    {
        var settings = UserSettings.Default.WithLabel("Work", "work").WithLabel("Bills").WithLabel("work");

        Assert.Equal(["Bills", "work"], settings.Labels);
        Assert.Equal("work", settings.ColorOf("WORK"));
        Assert.Null(settings.ColorOf("Bills"));
    }

    [Fact]
    public void A_Label_Is_Renamed_With_Its_Color_And_Forgotten_With_It()
    {
        var settings = UserSettings.Default.WithLabel("Work", "work").WithRenamedLabel("Work", "Job");

        Assert.Equal(["Job"], settings.Labels);
        Assert.Equal("work", settings.ColorOf("Job"));
        Assert.Null(settings.ColorOf("Work"));
        Assert.Empty(settings.WithoutLabel("job").Labels);
        Assert.Empty(settings.WithoutLabel("job").LabelColors);
    }

    [Fact]
    public void A_Color_Is_Changed_Or_Taken_Away_With_An_Empty_Text()
    {
        var settings = UserSettings.Default.WithLabel("Work", "work").WithLabel("Work", "bills");

        Assert.Equal("bills", settings.ColorOf("Work"));
        Assert.Null(settings.WithLabel("Work", string.Empty).ColorOf("Work"));
        Assert.Equal("bills", settings.WithLabel("Work").ColorOf("Work")); // no color given: it stays
    }

    [Fact]
    public void At_Most_The_Limit_Of_Labels_Can_Be_Created()
    {
        var atLimit = Enumerable.Range(0, UserSettings.MaxLabels).Aggregate(UserSettings.Default, (s, i) => s.WithLabel($"l{i}"));

        Assert.True(atLimit.IsValid(out _));
        Assert.False(atLimit.WithLabel("one more").IsValid(out _));
    }

    [Fact]
    public void The_Palette_Is_The_Three_Colors_Of_The_Design()
    {
        Assert.Equal(["work", "bills", "personal"], UserSettings.LabelPalette);
    }
}
