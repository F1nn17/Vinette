using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public class AttachmentFileNameTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("Отчёт №1.pdf", "Отчёт №1.pdf")]
    [InlineData("../../setup.exe", "setup.exe")]
    [InlineData("..\\..\\windows\\system32\\evil.dll", "evil.dll")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("C:\\Users\\a\\x.txt", "x.txt")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("  spaced name.txt  ", "spaced name.txt")]
    [InlineData("...hidden", "hidden")]
    [InlineData("trailing.dots...", "trailing.dots")]
    public void A_Name_Becomes_A_Plain_File_Name(string raw, string expected)
    {
        Assert.Equal(expected, AttachmentFileName.Sanitize(raw, "fallback"));
    }

    [Theory]
    [InlineData("invoice\u202Efdp.exe")] // right-to-left override: "invoice" + reversed "exe.pdf"
    [InlineData("a\u0000b.txt")]
    [InlineData("a\r\nb.txt")]
    [InlineData("a\u200Bb.txt")]
    [InlineData("\u2066a\u2069.txt")]
    public void Control_Direction_And_Invisible_Characters_Are_Removed(string raw)
    {
        var name = AttachmentFileName.Sanitize(raw, "fallback");

        Assert.All(name, c => Assert.False(char.IsControl(c) || c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\u200B'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("../")]
    public void Nothing_Usable_Gives_The_Fallback(string? raw)
    {
        Assert.Equal("fallback", AttachmentFileName.Sanitize(raw, "fallback"));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.pdf", "_COM1.pdf")]
    [InlineData("console.txt", "console.txt")]
    public void Names_Windows_Reserves_Get_A_Prefix(string raw, string expected)
    {
        Assert.Equal(expected, AttachmentFileName.Sanitize(raw, "fallback"));
    }

    [Fact]
    public void A_Long_Name_Is_Cut_But_Keeps_Its_Extension()
    {
        var name = AttachmentFileName.Sanitize(new string('a', 500) + ".pdf", "fallback");

        Assert.Equal(AttachmentFileName.MaxLength, name.Length);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Long_Name_Is_Never_Cut_Inside_A_Character()
    {
        var name = AttachmentFileName.Sanitize(string.Concat(Enumerable.Repeat("😀", 200)) + ".txt", "fallback");

        Assert.True(name.Length <= AttachmentFileName.MaxLength);
        Assert.DoesNotContain('\uFFFD', name);
        Assert.False(char.IsHighSurrogate(name[name.LastIndexOf('.') - 1]));
    }

    [Fact]
    public void A_Fallback_Is_Required()
    {
        Assert.Throws<ArgumentException>(() => AttachmentFileName.Sanitize("a.txt", ""));
    }
}
