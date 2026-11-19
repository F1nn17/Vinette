using Vinette.Core.Settings;

namespace Vinette.Core.Tests;

/// <summary>The signatures among the user's settings (VIN-104): what is within the limits and what is refused.</summary>
public class SignatureSettingsTests
{
    private static Signature Sig(string id, string name = "Work", string text = "Regards") => new(id, name, text);

    private static readonly char Lf = (char)10;

    [Fact]
    public void Signatures_Within_The_Limits_Are_Valid_And_One_May_Be_The_Default()
    {
        var settings = UserSettings.Default with { Signatures = [Sig("a"), Sig("b", "Home", "Line one" + Lf + "Line two")], DefaultSignatureId = "b" };

        Assert.True(settings.IsValid(out var problem), problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Signature_Needs_A_Name(string name)
    {
        Assert.False((UserSettings.Default with { Signatures = [Sig("a", name)] }).IsValid(out _));
    }

    [Fact]
    public void A_Signature_Name_Or_Text_Over_The_Limit_Or_With_Control_Characters_Is_Refused()
    {
        Assert.False((UserSettings.Default with { Signatures = [Sig("a", new string('x', UserSettings.MaxSignatureNameLength + 1))] }).IsValid(out _));
        Assert.False((UserSettings.Default with { Signatures = [Sig("a", "n", new string('x', UserSettings.MaxSignatureTextLength + 1))] }).IsValid(out _));
        Assert.False((UserSettings.Default with { Signatures = [Sig("a", "n" + (char)7)] }).IsValid(out _));
        Assert.False((UserSettings.Default with { Signatures = [Sig("a", "n", "text" + (char)0)] }).IsValid(out _));
    }

    [Fact]
    public void Tabs_And_Line_Breaks_Are_Part_Of_A_Text()
    {
        var text = "a" + (char)9 + "b" + (char)13 + Lf + "c";

        Assert.True((UserSettings.Default with { Signatures = [Sig("a", "n", text)] }).IsValid(out _));
    }

    [Fact]
    public void Too_Many_Signatures_Or_Repeated_Ids_Are_Refused()
    {
        var many = Enumerable.Range(0, UserSettings.MaxSignatures + 1).Select(i => Sig("id" + i)).ToList();

        Assert.False((UserSettings.Default with { Signatures = many }).IsValid(out _));
        Assert.False((UserSettings.Default with { Signatures = [Sig("a"), Sig("a", "Other")] }).IsValid(out _));
    }

    [Fact]
    public void The_Default_Signature_Must_Be_One_Of_The_Signatures()
    {
        Assert.False((UserSettings.Default with { Signatures = [Sig("a")], DefaultSignatureId = "missing" }).IsValid(out _));
        Assert.False((UserSettings.Default with { DefaultSignatureId = "a" }).IsValid(out _));
    }
}
