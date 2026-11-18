using Microsoft.Extensions.Options;
using Vinette.Api.Attachments;
using Vinette.Api.Configuration;
using Vinette.Core.Attachments;

namespace Vinette.Api.Tests;

/// <summary>The files that wait to be attached (VIN-093): kept, kept apart per mailbox, held to the limits, and never left behind. No mail server needed.</summary>
public sealed class FileAttachmentStoreTests : IDisposable
{
    private const string Alice = "alice@vinette.test";
    private const string Bob = "bob@vinette.test";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-attachment-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] Leftover = [1, 2, 3];
    private static readonly string[] LeftoverNames = ["aaaaaaaaaaaaaaaaaaaaaa.part", "bbbbbbbbbbbbbbbbbbbbbb.data"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private FileAttachmentStore Store(long maxFile = 1000, long maxTotal = 2500, int maxFiles = 3, TimeSpan? lifetime = null, TimeProvider? time = null) =>
        new(
            Options.Create(new AttachmentsOptions { Path = directory, MaxFileBytes = maxFile, MaxTotalBytes = maxTotal, MaxFiles = maxFiles, Lifetime = lifetime ?? TimeSpan.FromHours(24) }),
            time ?? TimeProvider.System);

    private static MemoryStream Bytes(int count, byte value = 7) => new(Enumerable.Repeat(value, count).ToArray());

    private string[] FilesOnDisk() => Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)] : [];

    [Fact]
    public async Task A_File_Is_Kept_As_It_Came_With_A_Clean_Name_A_Safe_Type_And_An_Opaque_Id()
    {
        var store = Store();
        var content = Enumerable.Range(0, 900).Select(i => (byte)(i % 251)).ToArray();

        var stored = await store.SaveAsync(Alice, "Договор №5.pdf", "application/pdf", new MemoryStream(content), Token);

        Assert.Matches("^[A-Za-z0-9_-]{22}$", stored.Id);
        Assert.Equal("Договор №5.pdf", stored.FileName);
        Assert.Equal("application/pdf", stored.ContentType);
        Assert.Equal(900, stored.Size);
        await using var opened = await store.OpenAsync(Alice, stored.Id, Token) ?? throw new InvalidOperationException("The file is not there.");
        using var read = new MemoryStream();
        await opened.Content.CopyToAsync(read, Token);
        Assert.Equal(content, read.ToArray());
        Assert.Equal(stored, opened.Attachment);
    }

    [Fact]
    public async Task A_Name_With_A_Path_Or_Control_Characters_Is_Cleaned_And_An_Odd_Type_Becomes_Plain_Bytes()
    {
        var store = Store();

        var one = await store.SaveAsync(Alice, "..\\..\\evil\u0007‮gnp.exe", "text/html", Bytes(10), Token);
        var two = await store.SaveAsync(Alice, "", "garbage", Bytes(10), Token);
        var three = await store.SaveAsync(Alice, "page.svg", null, Bytes(10), Token);

        Assert.DoesNotContain("/", one.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", one.FileName, StringComparison.Ordinal);
        Assert.All(one.FileName, c => Assert.False(char.IsControl(c)));
        Assert.Equal("application/octet-stream", one.ContentType);
        Assert.Equal("attachment", two.FileName);
        Assert.Equal("application/octet-stream", two.ContentType);
        Assert.Equal("application/octet-stream", three.ContentType);
    }

    [Fact]
    public async Task What_Is_Kept_Is_Listed_Oldest_First_With_The_Time_It_Was_Uploaded()
    {
        var time = new FixedTime(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        var store = Store(time: time);
        var first = await store.SaveAsync(Alice, "a.txt", "text/plain", Bytes(5), Token);
        time.Now = time.Now.AddMinutes(5);
        var second = await store.SaveAsync(Alice, "b.txt", "text/plain", Bytes(5), Token);

        var listed = await store.ListAsync(Alice, Token);

        Assert.Equal([first.Id, second.Id], listed.Select(a => a.Id));
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero), listed[0].UploadedAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 9, 5, 0, TimeSpan.Zero), listed[1].UploadedAt);
    }

    [Fact]
    public async Task A_Mailbox_Cannot_See_Open_Or_Remove_Another_Mailboxs_File_Whatever_Its_Id()
    {
        var store = Store();
        var alices = await store.SaveAsync(Alice, "secret.pdf", "application/pdf", Bytes(10), Token);

        Assert.Empty(await store.ListAsync(Bob, Token));
        Assert.Null(await store.OpenAsync(Bob, alices.Id, Token));
        Assert.False(await store.RemoveAsync(Bob, alices.Id, Token));
        Assert.NotNull(await store.OpenAsync(Alice, alices.Id, Token)); // still there
    }

    [Fact]
    public async Task The_Address_Is_Compared_Without_Regard_To_Case_Or_Spaces_Around_It()
    {
        var store = Store();
        var stored = await store.SaveAsync("Alice@Vinette.TEST", "a.txt", "text/plain", Bytes(5), Token);

        Assert.Single(await store.ListAsync("  alice@vinette.test ", Token));
        Assert.NotNull(await store.OpenAsync("ALICE@VINETTE.TEST", stored.Id, Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaa/a")]
    [InlineData("aaaaaaaaaaaaaaaaaaaa.a")]
    public async Task An_Id_That_Is_Not_One_Leads_Nowhere_And_Never_Out_Of_The_Directory(string id)
    {
        var store = Store();
        _ = await store.SaveAsync(Alice, "a.txt", "text/plain", Bytes(5), Token);

        Assert.Null(await store.OpenAsync(Alice, id, Token));
        Assert.False(await store.RemoveAsync(Alice, id, Token));
        Assert.Single(await store.ListAsync(Alice, Token));
    }

    [Fact]
    public async Task A_File_Exactly_As_Big_As_The_Limit_Is_Kept_And_One_Byte_More_Is_Refused_And_Leaves_Nothing()
    {
        var store = Store(maxFile: 1000, maxTotal: 5000);

        var exact = await store.SaveAsync(Alice, "exact.bin", null, Bytes(1000), Token);
        var refused = await Record.ExceptionAsync(() => store.SaveAsync(Alice, "over.bin", null, Bytes(1001), Token));

        Assert.Equal(1000, exact.Size);
        Assert.Equal(AttachmentRefusal.FileTooLarge, Assert.IsType<AttachmentRefusedException>(refused).Reason);
        Assert.Equal(2, FilesOnDisk().Length); // the one file: its data and its description; nothing of the refused one
        Assert.Single(await store.ListAsync(Alice, Token));
    }

    [Fact]
    public async Task All_The_Files_Together_Are_Held_To_The_Total_And_What_Is_Taken_Off_Makes_Room_Again()
    {
        var store = Store(maxFile: 1000, maxTotal: 2500, maxFiles: 10);
        var first = await store.SaveAsync(Alice, "1", null, Bytes(1000), Token);
        _ = await store.SaveAsync(Alice, "2", null, Bytes(1000), Token);

        var refused = await Record.ExceptionAsync(() => store.SaveAsync(Alice, "3", null, Bytes(600), Token));
        _ = await store.RemoveAsync(Alice, first.Id, Token);
        var fits = await store.SaveAsync(Alice, "3 again", null, Bytes(600), Token);

        Assert.Equal(AttachmentRefusal.TotalTooLarge, Assert.IsType<AttachmentRefusedException>(refused).Reason);
        Assert.Equal(600, fits.Size);
        Assert.Equal(2, (await store.ListAsync(Alice, Token)).Count);
    }

    [Fact]
    public async Task The_Total_Is_Per_Mailbox_And_Does_Not_Take_Room_From_Another()
    {
        var store = Store(maxFile: 1000, maxTotal: 1000);
        _ = await store.SaveAsync(Alice, "a", null, Bytes(1000), Token);

        var bobs = await store.SaveAsync(Bob, "b", null, Bytes(1000), Token);

        Assert.Equal(1000, bobs.Size);
    }

    private sealed class NeverRead : Stream
    {
        public bool WasRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task When_Too_Many_Files_Wait_The_Next_Is_Refused_Before_Its_Bytes_Are_Read()
    {
        var store = Store(maxFiles: 2, maxTotal: 10_000);
        _ = await store.SaveAsync(Alice, "1", null, Bytes(10), Token);
        _ = await store.SaveAsync(Alice, "2", null, Bytes(10), Token);
        var never = new NeverRead();

        var refused = await Record.ExceptionAsync(() => store.SaveAsync(Alice, "3", null, never, Token));

        Assert.Equal(AttachmentRefusal.TooManyFiles, Assert.IsType<AttachmentRefusedException>(refused).Reason);
        Assert.False(never.WasRead);
    }

    private sealed class Endless : Stream
    {
        public long Delivered { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Delivered += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_Upload_That_Never_Ends_Is_Stopped_Soon_After_The_Limit_And_Is_Never_Held_In_Memory()
    {
        var store = Store(maxFile: 1_000_000, maxTotal: 1_000_000);
        var endless = new Endless();

        var refused = await Record.ExceptionAsync(() => store.SaveAsync(Alice, "forever", null, endless, Token));

        Assert.IsType<AttachmentRefusedException>(refused);
        Assert.InRange(endless.Delivered, 1_000_001, 1_000_000 + 2 * 81920); // read only a buffer or so beyond the limit
        Assert.Empty(FilesOnDisk());
    }

    private sealed class Breaking(int after) : Stream
    {
        private int given;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = after - given;
            if (remaining <= 0)
            {
                throw new IOException("The connection was lost.");
            }

            var n = Math.Min(count, remaining);
            Array.Fill(buffer, (byte)7, offset, n);
            given += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_Upload_That_Breaks_Off_Or_Is_Cancelled_Leaves_Nothing_Behind()
    {
        var store = Store(maxFile: 100_000, maxTotal: 100_000);

        var broken = await Record.ExceptionAsync(() => store.SaveAsync(Alice, "broken", null, new Breaking(5000), Token));
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var cancelled = await Record.ExceptionAsync(() => store.SaveAsync(Alice, "cancelled", null, new Endless(), cancel.Token));

        Assert.IsType<IOException>(broken);
        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
        Assert.Empty(FilesOnDisk());
        Assert.Empty(await store.ListAsync(Alice, Token));
    }

    [Fact]
    public async Task Uploads_Started_Together_Are_Held_Exactly_To_The_Limits()
    {
        var store = Store(maxFile: 2000, maxTotal: 5000, maxFiles: 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            store.SaveAsync(Alice, $"f{i}", null, Bytes(2000), Token).ContinueWith(t => t.IsCompletedSuccessfully, TaskScheduler.Default)));

        Assert.Equal(2, results.Count(ok => ok)); // 2 x 2000 fit into 5000, a third does not
        Assert.Equal(2, (await store.ListAsync(Alice, Token)).Count);
    }

    [Fact]
    public async Task Removing_A_File_Removes_Its_Bytes_Too_And_Says_Whether_It_Was_There()
    {
        var store = Store();
        var stored = await store.SaveAsync(Alice, "a.txt", "text/plain", Bytes(5), Token);

        Assert.True(await store.RemoveAsync(Alice, stored.Id, Token));
        Assert.False(await store.RemoveAsync(Alice, stored.Id, Token));

        Assert.Empty(FilesOnDisk());
        Assert.Null(await store.OpenAsync(Alice, stored.Id, Token));
    }

    [Fact]
    public async Task A_File_That_Is_Open_Can_Still_Be_Removed_And_Read_To_The_End()
    {
        var store = Store();
        var stored = await store.SaveAsync(Alice, "a.txt", "text/plain", Bytes(100), Token);
        await using var opened = await store.OpenAsync(Alice, stored.Id, Token) ?? throw new InvalidOperationException();

        _ = await store.RemoveAsync(Alice, stored.Id, Token);

        using var read = new MemoryStream();
        await opened.Content.CopyToAsync(read, Token);
        Assert.Equal(100, read.Length);
    }

    [Fact]
    public async Task Something_In_The_Directory_That_Is_Not_Ours_Is_Not_An_Attachment()
    {
        var store = Store();
        var stored = await store.SaveAsync(Alice, "a.txt", "text/plain", Bytes(5), Token);
        var owner = Directory.EnumerateDirectories(directory).Single();
        await File.WriteAllTextAsync(Path.Combine(owner, "broken.json"), "{ not json", Token);
        await File.WriteAllTextAsync(Path.Combine(owner, "aaaaaaaaaaaaaaaaaaaaaa.json"), """{"id":"aaaaaaaaaaaaaaaaaaaaaa","fileName":"x","contentType":"x","size":1,"uploadedAt":"2026-01-01T00:00:00Z"}""", Token); // no bytes beside it

        var listed = await store.ListAsync(Alice, Token);

        Assert.Equal(stored.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task Files_That_Waited_Longer_Than_The_Lifetime_Are_Removed_And_Fresh_Ones_Stay()
    {
        var store = Store(lifetime: TimeSpan.FromHours(24));
        var old = await store.SaveAsync(Alice, "old.txt", "text/plain", Bytes(5), Token);
        var fresh = await store.SaveAsync(Alice, "fresh.txt", "text/plain", Bytes(5), Token);
        var past = DateTime.UtcNow.AddHours(-25);
        foreach (var file in Directory.EnumerateFiles(directory, old.Id + ".*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, past);
        }

        var removed = await store.RemoveExpiredAsync(Token);

        Assert.Equal(1, removed);
        Assert.Null(await store.OpenAsync(Alice, old.Id, Token));
        Assert.NotNull(await store.OpenAsync(Alice, fresh.Id, Token));
    }

    [Fact]
    public async Task What_A_Broken_Upload_Left_Behind_Is_Removed_When_It_Is_Old_And_An_Empty_Mailbox_Directory_Goes_With_It()
    {
        var store = Store();
        var owner = Path.Combine(directory, "0123456789abcdef0123456789abcdef");
        _ = Directory.CreateDirectory(owner);
        var leftovers = LeftoverNames.Select(n => Path.Combine(owner, n)).ToArray();
        foreach (var file in leftovers)
        {
            await File.WriteAllBytesAsync(file, Leftover, Token);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-30));
        }

        _ = await store.RemoveExpiredAsync(Token);

        Assert.Empty(FilesOnDisk());
        Assert.False(Directory.Exists(owner));
    }

    [Fact]
    public async Task Cleaning_Up_Before_Anything_Was_Ever_Uploaded_Is_Harmless()
    {
        Assert.Equal(0, await Store().RemoveExpiredAsync(Token));
    }

    [Fact]
    public async Task No_Address_Ever_Becomes_Part_Of_A_Path()
    {
        var store = Store();
        _ = await store.SaveAsync("../../weird name@vinette.test", "a.txt", "text/plain", Bytes(5), Token);

        Assert.All(FilesOnDisk(), file => Assert.DoesNotContain("weird", file, StringComparison.OrdinalIgnoreCase));
        Assert.All(FilesOnDisk(), file => Assert.StartsWith(directory, Path.GetFullPath(file), StringComparison.Ordinal));
    }
}
