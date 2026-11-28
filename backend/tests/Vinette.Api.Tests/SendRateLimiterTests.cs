using Microsoft.Extensions.Options;
using Vinette.Api.Security;

namespace Vinette.Api.Tests;

public sealed class SendRateLimiterTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private readonly ManualTimeProvider time = new();

    private SendRateLimiter Limiter(int messagesPerHour = 3, int recipientsPerHour = 0, int messagesPerDay = 0, int recipientsPerDay = 0) =>
        new(
            Options.Create(new SendRateLimitOptions
            {
                MessagesPerHour = messagesPerHour,
                RecipientsPerHour = recipientsPerHour,
                MessagesPerDay = messagesPerDay,
                RecipientsPerDay = recipientsPerDay,
            }),
            time);

    [Fact]
    public void Messages_Up_To_The_Hourly_Limit_Pass_And_The_Next_One_Waits_Until_The_Oldest_Is_An_Hour_Old()
    {
        var limiter = Limiter(messagesPerHour: 3);

        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
        time.Advance(TimeSpan.FromMinutes(10));

        var wait = limiter.TryAcquire("alice@vinette.test", 1);

        Assert.Equal(TimeSpan.FromMinutes(30), wait); // the first message leaves the hour 30 minutes from now
    }

    [Fact]
    public void After_The_Wait_The_Same_Send_Passes()
    {
        var limiter = Limiter(messagesPerHour: 1);
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));

        var wait = limiter.TryAcquire("alice@vinette.test", 1);
        time.Advance(wait!.Value);

        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
    }

    [Fact]
    public void A_Refused_Send_Is_Not_Written_Down()
    {
        var limiter = Limiter(messagesPerHour: 1);
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(limiter.TryAcquire("alice@vinette.test", 1));
        }

        time.Advance(TimeSpan.FromMinutes(60));

        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1)); // five refusals did not push the end of the wait away
    }

    [Fact]
    public void Recipients_Are_Counted_Apart_From_Messages()
    {
        var limiter = Limiter(messagesPerHour: 0, recipientsPerHour: 10);

        Assert.Null(limiter.TryAcquire("alice@vinette.test", 6));
        Assert.NotNull(limiter.TryAcquire("alice@vinette.test", 5)); // 6 + 5 > 10
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 4));
    }

    [Fact]
    public void One_Message_To_More_People_Than_The_Limit_Allows_Never_Passes_And_Is_Told_To_Wait_A_Whole_Window()
    {
        var limiter = Limiter(messagesPerHour: 0, recipientsPerHour: 10);

        var wait = limiter.TryAcquire("alice@vinette.test", 11);

        Assert.Equal(TimeSpan.FromHours(1), wait);
    }

    [Fact]
    public void The_Daily_Limit_Holds_Even_When_The_Hourly_One_Is_Spared()
    {
        var limiter = Limiter(messagesPerHour: 0, messagesPerDay: 3);
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
        time.Advance(TimeSpan.FromHours(5));
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
        time.Advance(TimeSpan.FromHours(5));
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
        time.Advance(TimeSpan.FromHours(5));

        var wait = limiter.TryAcquire("alice@vinette.test", 1);

        Assert.Equal(TimeSpan.FromHours(9), wait); // the first message is 24 hours old 9 hours from now
    }

    [Fact]
    public void The_Longer_Of_The_Hourly_And_The_Daily_Wait_Is_Told()
    {
        var limiter = Limiter(messagesPerHour: 5, messagesPerDay: 2);
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));

        var wait = limiter.TryAcquire("alice@vinette.test", 1);

        Assert.Equal(TimeSpan.FromHours(24), wait);
    }

    [Fact]
    public void Mailboxes_Are_Counted_Each_On_Its_Own_And_Names_Are_Not_Case_Sensitive()
    {
        var limiter = Limiter(messagesPerHour: 1);

        Assert.Null(limiter.TryAcquire("alice@vinette.test", 1));
        Assert.Null(limiter.TryAcquire("bob@vinette.test", 1));
        Assert.NotNull(limiter.TryAcquire("ALICE@vinette.test", 1));
    }

    [Fact]
    public void Zero_Means_No_Limit()
    {
        var limiter = Limiter(messagesPerHour: 0);

        for (var i = 0; i < 500; i++)
        {
            Assert.Null(limiter.TryAcquire("alice@vinette.test", 50));
        }

        Assert.Equal(0, limiter.TrackedMailboxes); // nothing is kept when there is nothing to limit
    }

    [Fact]
    public void Mailboxes_That_Sent_Nothing_For_A_Day_Are_Forgotten_And_The_Memory_Is_Bounded()
    {
        var limiter = new SendRateLimiter(Options.Create(new SendRateLimitOptions { MessagesPerHour = 10 }), time) { MaxTrackedMailboxes = 3 };
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(limiter.TryAcquire($"user{i}@vinette.test", 1));
        }

        time.Advance(TimeSpan.FromHours(25));
        limiter.Purge();
        Assert.Equal(0, limiter.TrackedMailboxes);

        for (var i = 0; i < 20; i++)
        {
            _ = limiter.TryAcquire($"flood{i}@vinette.test", 1);
        }

        Assert.True(limiter.TrackedMailboxes <= 3);
    }

    [Fact]
    public void The_Sends_Of_A_Hot_Mailbox_Are_Spread_Over_The_Hour_When_Counted_By_Recipients()
    {
        var limiter = Limiter(messagesPerHour: 0, recipientsPerHour: 100);
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 60));
        time.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(limiter.TryAcquire("alice@vinette.test", 40));
        time.Advance(TimeSpan.FromMinutes(20));

        // 30 more fit only after the first 60 have left the hour: 20 minutes from now
        Assert.Equal(TimeSpan.FromMinutes(20), limiter.TryAcquire("alice@vinette.test", 30));
    }
}
