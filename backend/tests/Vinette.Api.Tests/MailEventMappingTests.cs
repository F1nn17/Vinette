using System.Text.Json;
using Vinette.Api.Hubs;
using Vinette.Core.Mail;

namespace Vinette.Api.Tests;

/// <summary>What the real-time channel sends for each mailbox event (VIN-083): the wire form, as JSON.</summary>
public sealed class MailEventMappingTests
{
    private static readonly MessageId Id = new("INBOX", 12, 34);

    private static readonly MessageListItem Item = new(
        Id,
        new MailAddressInfo("Anna", "anna@example.org"),
        [new MailAddressInfo("", "bob@example.org")],
        "Hello",
        new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero),
        Seen: false,
        Flagged: true,
        Answered: false,
        Draft: false,
        HasAttachments: true,
        Labels: ["Work"],
        Size: 1234,
        Preview: string.Empty);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Wire(MailboxEvent change)
    {
        var dto = MailEventMapping.ToDto(change);
        return JsonSerializer.SerializeToElement(dto, dto.GetType(), Web);
    }

    [Fact]
    public void An_Arrival_Carries_The_Line_Of_The_List()
    {
        var json = Wire(new MessageArrived("INBOX", Item));

        Assert.Equal("arrived", json.GetProperty("type").GetString());
        Assert.Equal("INBOX", json.GetProperty("folder").GetString());
        var message = json.GetProperty("message");
        Assert.Equal(Id.ToString(), message.GetProperty("id").GetString());
        Assert.Equal("Hello", message.GetProperty("subject").GetString());
        Assert.Equal("anna@example.org", message.GetProperty("from").GetProperty("address").GetString());
        Assert.True(message.GetProperty("flagged").GetBoolean());
        Assert.True(message.GetProperty("hasAttachments").GetBoolean());
        Assert.Equal("Work", message.GetProperty("labels")[0].GetString());
    }

    [Fact]
    public void A_Removal_Says_How_Many()
    {
        var json = Wire(new MessagesRemoved("INBOX", 3));

        Assert.Equal("removed", json.GetProperty("type").GetString());
        Assert.Equal(3, json.GetProperty("count").GetInt32());
    }

    [Fact]
    public void A_Change_Of_Marks_Names_The_Message_By_Its_Identifier()
    {
        var json = Wire(new MessageMarksChanged("INBOX", Id, Seen: true, Flagged: false, Answered: true, ["Work", "Home"]));

        Assert.Equal("marks", json.GetProperty("type").GetString());
        Assert.Equal(Id.ToString(), json.GetProperty("id").GetString());
        Assert.True(json.GetProperty("seen").GetBoolean());
        Assert.False(json.GetProperty("flagged").GetBoolean());
        Assert.True(json.GetProperty("answered").GetBoolean());
        Assert.Equal(["Work", "Home"], json.GetProperty("labels").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public void Counters_And_Staleness_Are_Plain()
    {
        var counters = Wire(new FolderCountersChanged("INBOX", 50, 4));
        var stale = Wire(new FolderStale("INBOX"));

        Assert.Equal("counters", counters.GetProperty("type").GetString());
        Assert.Equal((50, 4), (counters.GetProperty("total").GetInt32(), counters.GetProperty("unread").GetInt32()));
        Assert.Equal("stale", stale.GetProperty("type").GetString());
        Assert.Equal("INBOX", stale.GetProperty("folder").GetString());
    }

    [Fact]
    public void An_Event_Nobody_Described_Is_Refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MailEventMapping.ToDto(new Unknown("INBOX")));
    }

    private sealed record Unknown(string Folder) : MailboxEvent(Folder);
}
