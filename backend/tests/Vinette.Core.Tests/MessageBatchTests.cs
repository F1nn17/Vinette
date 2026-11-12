using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

/// <summary>What can be one batch of messages.</summary>
public sealed class MessageBatchTests
{
    private static MessageId Id(uint uid, string folder = "INBOX", uint validity = 7) => new(folder, validity, uid);

    [Fact]
    public void One_To_The_Limit_Of_Messages_Of_One_Folder_Are_A_Batch()
    {
        Assert.True(MessageBatch.IsValid([Id(1)], out _));
        Assert.True(MessageBatch.IsValid([.. Enumerable.Range(1, MessageBatch.MaxMessages).Select(i => Id((uint)i))], out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void The_Limit_Is_A_Hundred()
    {
        Assert.Equal(100, MessageBatch.MaxMessages);
        Assert.False(MessageBatch.IsValid([.. Enumerable.Range(1, MessageBatch.MaxMessages + 1).Select(i => Id((uint)i))], out _));
    }

    [Fact]
    public void An_Empty_Batch_Is_Not_One()
    {
        Assert.False(MessageBatch.IsValid([], out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Messages_Of_Two_Folders_Or_Of_Two_Incarnations_Of_A_Folder_Are_Not_One_Batch()
    {
        Assert.False(MessageBatch.IsValid([Id(1), Id(2, "Work")], out _));
        Assert.False(MessageBatch.IsValid([Id(1), Id(2, validity: 8)], out _));
    }

    [Fact]
    public void A_Message_Named_Twice_Is_Refused()
    {
        Assert.False(MessageBatch.IsValid([Id(1), Id(1)], out _));
    }
}
