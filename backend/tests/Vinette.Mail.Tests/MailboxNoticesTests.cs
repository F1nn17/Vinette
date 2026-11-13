using MailKit;

namespace Vinette.Mail.Tests;

/// <summary>How the server's notifications are put together before they are answered (VIN-082). No mail server needed.</summary>
public sealed class MailboxNoticesTests
{
    [Fact]
    public void A_New_Collection_Is_Empty()
    {
        var notices = new MailboxNotices();

        Assert.True(notices.IsEmpty);
        Assert.True(notices.Take().IsEmpty);
    }

    [Fact]
    public void The_Reports_Are_Summed_Up_and_Taking_Them_Starts_Over()
    {
        var notices = new MailboxNotices();
        notices.CountChanged();
        notices.CountChanged();
        notices.Expunged();
        notices.Expunged(3);

        Assert.False(notices.IsEmpty);
        var batch = notices.Take();

        Assert.True(batch.CountChanged);
        Assert.Equal(4, batch.Expunged);
        Assert.False(batch.IsEmpty);
        Assert.True(notices.IsEmpty);
        Assert.True(notices.Take().IsEmpty);
    }

    [Fact]
    public void Several_Reports_About_One_Message_Count_As_One_And_The_Last_Wins()
    {
        var notices = new MailboxNotices();
        notices.Flags(7, 0, MessageFlags.Seen, []);
        notices.Flags(7, 0, MessageFlags.Seen | MessageFlags.Flagged, ["Work"]);
        notices.Flags(9, 1, MessageFlags.None, []);

        var batch = notices.Take();

        Assert.Equal([7u, 9u], batch.Marks.Keys.Order());
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, batch.Marks[7].Flags);
        Assert.Equal(["Work"], batch.Marks[7].Keywords);
        Assert.Empty(batch.MarksByPosition);
    }

    [Fact]
    public void A_Report_Without_An_Identifier_Is_Kept_By_Position_And_The_Last_Wins()
    {
        var notices = new MailboxNotices();
        notices.Flags(null, 4, MessageFlags.Seen, []);
        notices.Flags(null, 4, MessageFlags.Seen | MessageFlags.Answered, []);

        var batch = notices.Take();

        Assert.Equal(MessageFlags.Seen | MessageFlags.Answered, Assert.Single(batch.MarksByPosition).Value.Flags);
        Assert.Equal(4, batch.MarksByPosition.Keys.Single());
        Assert.Empty(batch.Marks);
        Assert.False(batch.IsEmpty);
    }

    [Fact]
    public void A_Negative_Count_Of_Removals_Changes_Nothing()
    {
        var notices = new MailboxNotices();
        notices.Expunged(-2);

        Assert.True(notices.Take().IsEmpty);
    }

    [Fact]
    public async Task Reports_From_Several_Threads_Are_All_Counted()
    {
        var notices = new MailboxNotices();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                notices.Expunged();
            }
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(8000, notices.Take().Expunged);
    }
}
