using System.Text.Json;
using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public sealed class MessageIdTests
{
    [Theory]
    [InlineData("INBOX")]
    [InlineData("Work/Reports 2026")]
    [InlineData("Отправленные")]
    [InlineData("A.B.C")] // a dot in the folder name must not break the id
    [InlineData("a")]
    [InlineData("a b+c/d?e#f%g")]
    [InlineData("🙂")]
    public void An_Id_Survives_A_Round_Trip_Through_Its_Text(string folder)
    {
        var id = new MessageId(folder, 1731000000, 42);

        var parsed = MessageId.TryParse(id.ToString(), out var back);

        Assert.True(parsed);
        Assert.Equal(id, back);
        Assert.DoesNotContain('/', id.ToString()); // safe in a URL path without escaping
        Assert.DoesNotContain('+', id.ToString());
        Assert.DoesNotContain('=', id.ToString());
    }

    [Fact]
    public void The_Text_Is_Stable()
    {
        // Ids are cached by clients as text: changing this changes every id. Do not edit without a migration.
        Assert.Equal("SU5CT1g.1731000000.42", new MessageId("INBOX", 1731000000, 42).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("INBOX")]
    [InlineData("SU5CT1g.1731000000")]
    [InlineData("SU5CT1g.1731000000.42.1")]
    [InlineData("SU5CT1g.0.42")] // UIDVALIDITY and UID are never zero
    [InlineData("SU5CT1g.1731000000.0")]
    [InlineData("SU5CT1g.01731000000.42")] // a second spelling of the same id
    [InlineData("SU5CT1g.1731000000.+42")]
    [InlineData("SU5CT1g.1731000000.-1")]
    [InlineData("SU5CT1g.4294967296.42")] // over uint
    [InlineData("SU5CT1g=.1731000000.42")] // padding is not part of the form
    [InlineData("SU5CT1h.1731000000.42")] // same bytes, other unused trailing bits
    [InlineData("SU5CT1g+.1731000000.42")]
    [InlineData("!!.1.1")]
    [InlineData(".1.1")]
    [InlineData("/w.1.1")] // base64 of invalid UTF-8
    [InlineData("INBOX.1.1.")]
    public void Anything_Else_Is_Refused(string? text)
    {
        Assert.False(MessageId.TryParse(text, out _));
    }

    [Fact]
    public void A_Huge_Text_Is_Refused_Cheaply()
    {
        Assert.False(MessageId.TryParse(new string('A', 100_000) + ".1.1", out _));
    }

    [Fact]
    public void Parse_Throws_On_Garbage()
    {
        Assert.Throws<FormatException>(() => MessageId.Parse("garbage", null));
    }

    [Theory]
    [InlineData("", 1u, 1u)]
    [InlineData("INBOX", 0u, 1u)]
    [InlineData("INBOX", 1u, 0u)]
    public void An_Id_Cannot_Be_Built_From_Impossible_Parts(string folder, uint uidValidity, uint uid)
    {
        Assert.ThrowsAny<ArgumentException>(() => new MessageId(folder, uidValidity, uid));
    }

    [Fact]
    public void Ids_Compare_By_Value()
    {
        Assert.Equal(new MessageId("INBOX", 1, 2), new MessageId("INBOX", 1, 2));
        Assert.NotEqual(new MessageId("INBOX", 1, 2), new MessageId("INBOX", 3, 2)); // same UID, recreated folder
    }

    [Fact]
    public void Json_Uses_The_Text_Form_And_Refuses_Garbage()
    {
        var id = new MessageId("Отправленные", 7, 9);

        var json = JsonSerializer.Serialize(new { id });
        var back = JsonSerializer.Deserialize<MessageId>(JsonSerializer.Serialize(id.ToString()));

        Assert.Equal($"{{\"id\":\"{id}\"}}", json);
        Assert.Equal(id, back);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MessageId>("\"nope\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MessageId>("5"));
    }

    [Fact]
    public void The_Same_UidValidity_Is_Accepted()
    {
        new MessageId("INBOX", 100, 1).EnsureCurrent(100);
    }

    [Fact]
    public void A_Changed_UidValidity_Is_Reported_With_Both_Values()
    {
        var id = new MessageId("INBOX", 100, 1);

        var error = Assert.Throws<UidValidityChangedException>(() => id.EnsureCurrent(101));

        Assert.Equal((100u, 101u, "INBOX"), (error.Requested, error.Current, error.Folder));
        Assert.DoesNotContain("INBOX", error.Message, StringComparison.Ordinal); // the name is the user's data
    }
}
