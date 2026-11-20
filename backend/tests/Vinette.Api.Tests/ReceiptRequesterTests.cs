using Vinette.Api.Mail;
using Vinette.Core.Mail;

namespace Vinette.Api.Tests;

/// <summary>Who may be asked about a read receipt (VIN-106): only the sender, and only once.</summary>
public sealed class ReceiptRequesterTests
{
    private static MailAddressInfo Person(string address) => new(string.Empty, address);

    private static ParsedMessage Parsed(MailAddressInfo? from, MailAddressInfo? receiptTo) =>
        new(string.Empty, from, [], [], null, null, null, null, [], [], [], ReceiptTo: receiptTo);

    private static MessageSource Source(bool receiptSent = false, bool draft = false) =>
        new(new MessageId("INBOX", 1, 1), [], false, false, false, draft, [], receiptSent);

    [Fact]
    public void The_Sender_Who_Asks_Is_The_Requester_Whatever_The_Case()
    {
        var requester = ReceiptEndpoints.Requester(Parsed(Person("Anna@Example.org"), Person("anna@example.org")), Source());

        Assert.Equal("anna@example.org", requester?.Address);
    }

    [Fact]
    public void A_Request_That_Points_Somewhere_Else_Is_Ignored_It_Would_Tell_A_Third_Party_When_The_Message_Was_Opened()
    {
        Assert.Null(ReceiptEndpoints.Requester(Parsed(Person("anna@example.org"), Person("spy@elsewhere.example")), Source()));
    }

    [Fact]
    public void Nobody_Is_Asked_When_There_Is_No_Request_No_Sender_Or_It_Is_Answered_Or_A_Draft()
    {
        Assert.Null(ReceiptEndpoints.Requester(Parsed(Person("anna@example.org"), null), Source()));
        Assert.Null(ReceiptEndpoints.Requester(Parsed(null, Person("anna@example.org")), Source()));
        Assert.Null(ReceiptEndpoints.Requester(Parsed(Person("anna@example.org"), Person("anna@example.org")), Source(receiptSent: true)));
        Assert.Null(ReceiptEndpoints.Requester(Parsed(Person("anna@example.org"), Person("anna@example.org")), Source(draft: true)));
        Assert.Null(ReceiptEndpoints.Requester(Parsed(Person("anna@example.org"), Person(" ")), Source()));
    }
}
