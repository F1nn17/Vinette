using MailKit.Net.Smtp;
using MimeKit;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>How the answers of a submission service become the reasons the user is told (no mail server needed).</summary>
public sealed class SmtpRefusalTests
{
    private static SmtpCommandException Answer(SmtpErrorCode code, SmtpStatusCode status, string? mailbox = null) =>
        new(code, status, MailboxAddress.Parse(mailbox ?? "someone@example.org"), "the server's own words, with bob@example.org in them");

    [Fact]
    public void A_Refused_Recipient_Says_Which_One()
    {
        var rejected = SmtpMailSender.Refusal(Answer(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "ann@example.org"));

        Assert.Equal(SendRejection.Recipient, rejected.Reason);
        Assert.Equal("ann@example.org", rejected.Address);
    }

    [Fact]
    public void A_Refused_Sender_Is_Not_A_Recipient_Problem()
    {
        var rejected = SmtpMailSender.Refusal(Answer(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxNameNotAllowed, "me@example.org"));

        Assert.Equal(SendRejection.Sender, rejected.Reason);
        Assert.Null(rejected.Address);
    }

    [Theory]
    [InlineData(SmtpErrorCode.SenderNotAccepted)] // the size is announced with the sender, where the server says "too much"
    [InlineData(SmtpErrorCode.MessageNotAccepted)]
    [InlineData(SmtpErrorCode.UnexpectedStatusCode)]
    public void Too_Much_Is_Too_Large_Wherever_The_Server_Says_It(SmtpErrorCode code)
    {
        Assert.Equal(SendRejection.TooLarge, SmtpMailSender.Refusal(Answer(code, SmtpStatusCode.ExceededStorageAllocation)).Reason);
    }

    [Fact]
    public void A_Message_The_Server_Does_Not_Like_Is_A_Content_Problem()
    {
        Assert.Equal(SendRejection.Content, SmtpMailSender.Refusal(Answer(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed)).Reason);
    }

    [Fact]
    public void Anything_Else_Is_Other()
    {
        Assert.Equal(SendRejection.Other, SmtpMailSender.Refusal(Answer(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.TransactionFailed)).Reason);
    }

    [Fact]
    public void What_The_Server_Said_Stays_In_The_Inner_Exception_For_The_Log_And_Out_Of_The_Message()
    {
        var rejected = SmtpMailSender.Refusal(Answer(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "ann@example.org"));

        Assert.DoesNotContain("bob@example.org", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("bob@example.org", rejected.InnerException!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, true)]
    [InlineData(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.MailboxBusy, true)] // 450: a temporary refusal is still a refusal
    [InlineData(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.ExceededStorageAllocation, true)]
    [InlineData(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.Ok, false)] // not an error answer at all
    public void Only_An_Answer_That_Refuses_Counts_As_A_Refusal(SmtpErrorCode code, SmtpStatusCode status, bool refusal)
    {
        Assert.Equal(refusal, SmtpMailSender.IsRefusal(Answer(code, status)));
    }
}
