using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public sealed class PreviewTextTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   \r\n\t ", "")]
    [InlineData("Hello", "Hello")]
    [InlineData("  Hello \r\n\r\n  world\t!  ", "Hello world !")]
    [InlineData("a  b", "a b")] // non-breaking spaces
    [InlineData("a​b", "ab")] // zero-width space
    [InlineData("‮evil‬", "evil")] // bidirectional override: cannot be used to flip the text
    [InlineData("a\u0000b\u0007c", "abc")] // control characters
    [InlineData("a﻿b", "ab")] // byte order mark
    [InlineData("Привет, мир", "Привет, мир")]
    public void Text_Becomes_One_Clean_Line(string? input, string expected)
    {
        Assert.Equal(expected, PreviewText.Normalize(input));
    }

    [Fact]
    public void A_Long_Text_Is_Cut_With_An_Ellipsis_At_The_Limit()
    {
        var result = PreviewText.Normalize(new string('x', 1000));

        Assert.Equal(PreviewText.MaxLength + 1, result.Length);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Text_Exactly_At_The_Limit_Is_Kept_Whole()
    {
        var text = new string('x', PreviewText.MaxLength);

        Assert.Equal(text, PreviewText.Normalize(text));
    }

    [Fact]
    public void The_Cut_Never_Splits_A_Character_Made_Of_Two_Code_Units()
    {
        var text = new string('x', PreviewText.MaxLength - 1) + "😀😀";

        var result = PreviewText.Normalize(text);

        Assert.EndsWith("😀…", result, StringComparison.Ordinal);
        _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(result); // throws on a lone surrogate
    }

    [Fact]
    public void An_Emoji_Sequence_Keeps_Its_Joiners()
    {
        const string family = "👨‍👩‍👧";

        Assert.Equal(family, PreviewText.Normalize(family));
    }
}
