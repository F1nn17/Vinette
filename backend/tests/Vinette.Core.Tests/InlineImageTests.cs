using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public class InlineImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10];
    private static readonly byte[] Gif = "GIF89a\u0001\0\u0001\0"u8.ToArray();
    private static readonly byte[] Webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("gif", "image/gif")]
    [InlineData("webp", "image/webp")]
    public void The_Four_Common_Raster_Formats_Are_Recognized_By_Their_First_Bytes(string kind, string type)
    {
        byte[] bytes = kind switch { "png" => Png, "jpeg" => Jpeg, "gif" => Gif, _ => Webp };

        Assert.Equal(type, InlineImage.Sniff(bytes));
        Assert.StartsWith($"data:{type};base64,", InlineImage.ToDataUri(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void The_Data_Address_Holds_The_Same_Bytes()
    {
        var uri = InlineImage.ToDataUri(Png)!;

        Assert.Equal(Png, Convert.FromBase64String(uri[(uri.IndexOf(',', StringComparison.Ordinal) + 1)..]));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>")]
    [InlineData("<?xml version=\"1.0\"?><svg/>")]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("<!DOCTYPE html><title>x</title>")]
    [InlineData("%PDF-1.7")]
    [InlineData("MZ\u0090\0\u0003")]
    [InlineData("GIF90a")]
    [InlineData("RIFF\0\0\0\0WAVEfmt ")]
    [InlineData("plain text")]
    public void Anything_Else_Is_Not_A_Picture_Whatever_The_Sender_Calls_It(string content)
    {
        Assert.Null(InlineImage.Sniff(System.Text.Encoding.Latin1.GetBytes(content)));
        Assert.Null(InlineImage.ToDataUri(System.Text.Encoding.Latin1.GetBytes(content)));
    }

    [Fact]
    public void Nothing_And_Too_Much_Are_Refused()
    {
        Assert.Null(InlineImage.ToDataUri([]));
        var big = new byte[MessageLimits.MaxInlineImageBytes + 1];
        Png.CopyTo(big, 0);
        Assert.Null(InlineImage.ToDataUri(big));
        var exactly = new byte[MessageLimits.MaxInlineImageBytes];
        Png.CopyTo(exactly, 0);
        Assert.NotNull(InlineImage.ToDataUri(exactly));
    }

    [Fact]
    public void A_Short_Input_Cannot_Make_The_Check_Fail()
    {
        foreach (var length in Enumerable.Range(0, 14))
        {
            var bytes = new byte[length];
            Array.Fill(bytes, (byte)'R');
            Assert.Null(InlineImage.Sniff(bytes));
        }
    }
}
