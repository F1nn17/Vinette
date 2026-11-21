using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public sealed class MessageSearchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void An_Empty_Query_Has_No_Words(string? text) => Assert.Null(MessageSearch.Terms(text));

    [Fact]
    public void The_Words_Are_Split_At_Spaces_And_Keep_Their_Letters()
    {
        Assert.Equal(["Отчёт", "за", "Квартал"], MessageSearch.Terms("  Отчёт   за\tКвартал "));
    }

    [Fact]
    public void Control_Characters_Count_As_Spaces()
    {
        Assert.Equal(["a", "b"], MessageSearch.Terms("a\r\nb"));
        Assert.Equal(["a", "b"], MessageSearch.Terms("a\0b"));
    }

    [Fact]
    public void A_Search_Is_Recognised_By_Its_Words()
    {
        Assert.False(new MessageListQuery("INBOX", null, 10).IsSearch);
        Assert.False(new MessageListQuery("INBOX", null, 10, Terms: []).IsSearch);
        Assert.True(new MessageListQuery("INBOX", null, 10, Terms: ["a"]).IsSearch);
    }
}
