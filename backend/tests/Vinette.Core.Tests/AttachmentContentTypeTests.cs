using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public class AttachmentContentTypeTests
{
    [Theory]
    [InlineData("application/pdf")]
    [InlineData("APPLICATION/PDF")]
    [InlineData(" image/png ")]
    [InlineData("image/jpeg")]
    [InlineData("application/zip")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("text/plain")]
    [InlineData("text/csv")]
    [InlineData("message/rfc822")]
    [InlineData("video/mp4")]
    public void Harmless_Types_Are_Passed_On_In_Lower_Case(string declared)
    {
        Assert.Equal(declared.Trim().ToLowerInvariant(), AttachmentContentType.ForDownload(declared));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("TEXT/HTML")]
    [InlineData("application/xhtml+xml")]
    [InlineData("image/svg+xml")]
    [InlineData("text/xml")]
    [InlineData("application/xml")]
    [InlineData("text/javascript")]
    [InlineData("application/javascript")]
    [InlineData("application/x-javascript")]
    [InlineData("text/css")]
    [InlineData("application/x-shockwave-flash")]
    [InlineData("application/x-msdownload")]
    [InlineData("application/hta")]
    [InlineData("text/x-scriptlet")]
    [InlineData("multipart/mixed")]
    [InlineData("image/svg+xml; charset=utf-8")]
    [InlineData("text/html;x=y")]
    [InlineData("text/plain, text/html")]
    [InlineData("text/plain\r\nX-Evil: 1")]
    [InlineData("application/pdf\u0000")]
    [InlineData("../../etc/passwd")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("nonsense")]
    [InlineData("a/b")]
    public void Everything_Else_Is_Plain_Bytes(string? declared)
    {
        Assert.Equal(AttachmentContentType.Bytes, AttachmentContentType.ForDownload(declared));
    }

    [Fact]
    public void A_Very_Long_Type_Is_Plain_Bytes()
    {
        Assert.Equal(AttachmentContentType.Bytes, AttachmentContentType.ForDownload("application/" + new string('a', 5000)));
    }
}
