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

    [Fact]
    public void Nobody_Is_Trusted_With_Pictures_By_Default()
    {
        Assert.Empty(UserSettings.Default.TrustedImageSenders);
        Assert.False(UserSettings.Default.TrustsImagesFrom("anna@example.org"));
    }

    [Fact]
    public void A_Sender_Is_Added_Once_In_Lower_Case_And_The_List_Stays_Sorted()
    {
        var settings = UserSettings.Default
            .WithImageSender("  Zed@Example.org ", trusted: true)
            .WithImageSender("anna@example.org", trusted: true)
            .WithImageSender("ANNA@example.org", trusted: true);

        Assert.Equal(["anna@example.org", "zed@example.org"], settings.TrustedImageSenders);
        Assert.True(settings.TrustsImagesFrom("Anna@Example.ORG"));
        Assert.False(settings.TrustsImagesFrom("anna@example.com"));
        Assert.False(settings.TrustsImagesFrom(null));
        Assert.False(settings.TrustsImagesFrom("  "));
    }

    [Fact]
    public void A_Sender_Can_Be_Removed_And_Removing_Another_Changes_Nothing()
    {
        var settings = UserSettings.Default.WithImageSender("anna@example.org", trusted: true);

        Assert.Empty(settings.WithImageSender("ANNA@example.org", trusted: false).TrustedImageSenders);
        Assert.Equal(["anna@example.org"], settings.WithImageSender("zed@example.org", trusted: false).TrustedImageSenders);
    }

    [Fact]
    public void At_Most_The_Limit_Of_Senders_Can_Be_Trusted()
    {
        var atLimit = Enumerable.Range(0, UserSettings.MaxTrustedImageSenders).Aggregate(UserSettings.Default, (s, i) => s.WithImageSender($"u{i}@example.org", trusted: true));

        Assert.True(atLimit.IsValid(out _));
        Assert.False(atLimit.WithImageSender("one-more@example.org", trusted: true).IsValid(out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\nb@example.org")]
    public void A_Trusted_Sender_Must_Be_A_Plain_Address(string address)
    {
        Assert.False((UserSettings.Default with { TrustedImageSenders = [address] }).IsValid(out _));
    }

    [Fact]
    public void An_Address_Longer_Than_The_Limit_Is_Refused()
    {
        Assert.False((UserSettings.Default with { TrustedImageSenders = [new string('a', 255)] }).IsValid(out _));
    }
}
