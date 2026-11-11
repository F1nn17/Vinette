using Vinette.Core.Settings;

namespace Vinette.Core.Tests;

public class UserSettingsTests
{
    [Fact]
    public void The_Defaults_Are_Valid_And_Comfortable()
    {
        Assert.True(UserSettings.Default.IsValid(out var problem), problem);
        Assert.Equal(ListDensity.Comfortable, UserSettings.Default.Density);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData("en-GB")]
    [InlineData(null)]
    public void Language_Tags_Are_Accepted(string? language)
    {
        Assert.True((UserSettings.Default with { Language = language }).IsValid(out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ru en")]
    [InlineData("ru_RU")]
    [InlineData("русский")]
    [InlineData("12345678901234567")]
    public void Anything_That_Is_Not_A_Short_Language_Tag_Is_Not(string language)
    {
        Assert.False((UserSettings.Default with { Language = language }).IsValid(out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void An_Unknown_Density_Is_Not_Valid()
    {
        Assert.False((UserSettings.Default with { Density = (ListDensity)99 }).IsValid(out _));
    }

    [Fact]
    public void The_Limit_Of_Label_Colors_Is_Exact()
    {
        var atLimit = Enumerable.Range(0, UserSettings.MaxLabelColors).ToDictionary(i => $"l{i}", _ => "work");

        Assert.True((UserSettings.Default with { LabelColors = atLimit }).IsValid(out _));
        atLimit["one more"] = "work";
        Assert.False((UserSettings.Default with { LabelColors = atLimit }).IsValid(out _));
    }

    [Theory]
    [InlineData("Работа", "work", true)]
    [InlineData("a", "b", true)]
    [InlineData("", "work", false)]
    [InlineData("   ", "work", false)]
    [InlineData("name", "", false)]
    [InlineData("tab\there", "work", false)]
    [InlineData("name", "new\nline", false)]
    public void Label_Names_And_Colors_Are_Plain_Short_Text(string name, string color, bool valid)
    {
        Assert.Equal(valid, (UserSettings.Default with { LabelColors = new Dictionary<string, string> { [name] = color } }).IsValid(out _));
    }
}
