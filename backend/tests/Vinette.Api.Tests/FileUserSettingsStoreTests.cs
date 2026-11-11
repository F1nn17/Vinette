using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Settings;
using Vinette.Core.Settings;

namespace Vinette.Api.Tests;

public sealed class FileUserSettingsStoreTests : IDisposable
{
    private const string Alice = "alice@vinette.test";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-settings-" + Guid.NewGuid().ToString("N"));

    private readonly List<FileUserSettingsStore> created = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var store in created)
        {
            store.Dispose();
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private FileUserSettingsStore NewStore()
    {
        var store = new FileUserSettingsStore(Options.Create(new SettingsOptions { Path = directory }), NullLogger<FileUserSettingsStore>.Instance, TimeProvider.System);
        created.Add(store);
        return store;
    }

    private string[] Files() => Directory.Exists(directory) ? [.. Directory.GetFiles(directory).Select(f => Path.GetFileName(f))] : [];

    [Fact]
    public async Task A_User_Without_Saved_Settings_Gets_The_Defaults()
    {
        var settings = await NewStore().GetAsync(Alice, Token);

        Assert.Equal(UserSettings.Default, settings with { });
        Assert.Equal(ListDensity.Comfortable, settings.Density);
        Assert.Null(settings.Language);
        Assert.Empty(settings.LabelColors);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task What_Is_Saved_Is_Read_Back_Also_By_A_Store_Started_Later()
    {
        await NewStore().UpdateAsync(
            Alice,
            s => s with { Density = ListDensity.Compact, Language = "en", LabelColors = new Dictionary<string, string> { ["Работа"] = "work" } },
            Token);

        var settings = await NewStore().GetAsync(Alice, Token);

        Assert.Equal(ListDensity.Compact, settings.Density);
        Assert.Equal("en", settings.Language);
        Assert.Equal("work", settings.LabelColors["Работа"]);
    }

    [Fact]
    public async Task Settings_Of_Two_Users_Are_Kept_Apart()
    {
        var store = NewStore();

        await store.UpdateAsync(Alice, s => s with { Density = ListDensity.Compact }, Token);

        Assert.Equal(ListDensity.Comfortable, (await store.GetAsync("bob@vinette.test", Token)).Density);
    }

    [Fact]
    public async Task The_Address_Is_Case_Insensitive_And_Never_Shows_In_A_File_Name()
    {
        var store = NewStore();
        await store.UpdateAsync("Alice@Vinette.Test", s => s with { Density = ListDensity.Compact }, Token);

        var settings = await store.GetAsync("  alice@vinette.test ", Token);

        Assert.Equal(ListDensity.Compact, settings.Density);
        var file = Assert.Single(Files());
        Assert.DoesNotContain("alice", file, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".json", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changes_Made_At_The_Same_Time_Do_Not_Lose_Each_Other()
    {
        var store = NewStore();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => store.UpdateAsync(
            Alice,
            s => s with { LabelColors = new Dictionary<string, string>(s.LabelColors) { [$"label-{i}"] = "work" } },
            Token)));

        Assert.Equal(40, (await store.GetAsync(Alice, Token)).LabelColors.Count);
    }

    [Fact]
    public async Task A_Save_Leaves_No_Temporary_File_Behind()
    {
        var store = NewStore();

        for (var i = 0; i < 5; i++)
        {
            await store.UpdateAsync(Alice, s => s with { Language = i % 2 == 0 ? "ru" : "en" }, Token);
        }

        Assert.Single(Files());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ru en")]
    [InlineData("ru\n")]
    [InlineData("a-very-long-language-tag-indeed")]
    public async Task A_Wrong_Language_Is_Refused_And_Nothing_Is_Saved(string language)
    {
        var store = NewStore();

        await Assert.ThrowsAsync<InvalidSettingsException>(() => store.UpdateAsync(Alice, s => s with { Language = language }, Token));

        Assert.Empty(Files());
    }

    [Fact]
    public async Task Too_Many_Or_Strange_Label_Colors_Are_Refused()
    {
        var store = NewStore();
        var tooMany = Enumerable.Range(0, UserSettings.MaxLabelColors + 1).ToDictionary(i => $"l{i}", _ => "work");

        await Assert.ThrowsAsync<InvalidSettingsException>(() => store.UpdateAsync(Alice, s => s with { LabelColors = tooMany }, Token));
        await Assert.ThrowsAsync<InvalidSettingsException>(() => store.UpdateAsync(Alice, s => s with { LabelColors = new Dictionary<string, string> { [""] = "work" } }, Token));
        await Assert.ThrowsAsync<InvalidSettingsException>(() => store.UpdateAsync(Alice, s => s with { LabelColors = new Dictionary<string, string> { ["a\u0000b"] = "work" } }, Token));
        await Assert.ThrowsAsync<InvalidSettingsException>(() => store.UpdateAsync(Alice, s => s with { LabelColors = new Dictionary<string, string> { ["ok"] = new string('x', UserSettings.MaxTextLength + 1) } }, Token));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task A_Damaged_File_Is_Set_Aside_And_The_User_Starts_With_The_Defaults()
    {
        var store = NewStore();
        await store.UpdateAsync(Alice, s => s with { Density = ListDensity.Compact }, Token);
        var file = Directory.GetFiles(directory, "*.json").Single();
        await File.WriteAllTextAsync(file, "{ not json at all", Token);

        var settings = await store.GetAsync(Alice, Token);

        Assert.Equal(ListDensity.Comfortable, settings.Density);
        Assert.Contains(Files(), f => f.Contains(".damaged-", StringComparison.Ordinal)); // kept for the administrator
        await store.UpdateAsync(Alice, s => s with { Language = "ru" }, Token); // and saving works again
        Assert.Equal("ru", (await store.GetAsync(Alice, Token)).Language);
    }

    [Fact]
    public async Task A_File_That_Is_Far_Too_Large_Is_Not_Read()
    {
        var store = NewStore();
        await store.UpdateAsync(Alice, s => s with { Density = ListDensity.Compact }, Token);
        var file = Directory.GetFiles(directory, "*.json").Single();
        await File.WriteAllTextAsync(file, "{\"language\":\"" + new string('a', 300 * 1024) + "\"}", Token);

        var settings = await store.GetAsync(Alice, Token);

        Assert.Equal(UserSettings.Default.Density, settings.Density);
        Assert.Null(settings.Language);
    }

    [Fact]
    public async Task A_File_With_Values_Outside_The_Limits_Is_Treated_As_Damaged()
    {
        var store = NewStore();
        await store.UpdateAsync(Alice, s => s with { Density = ListDensity.Compact }, Token);
        var file = Directory.GetFiles(directory, "*.json").Single();
        await File.WriteAllTextAsync(file, "{\"density\":\"Compact\",\"language\":\"not a language\"}", Token);

        var settings = await store.GetAsync(Alice, Token);

        Assert.Equal(ListDensity.Comfortable, settings.Density);
    }

    [Fact]
    public async Task A_Document_Of_An_Older_Or_Newer_Version_Is_Read_Missing_Fields_Default_Unknown_Ones_Are_Ignored()
    {
        var store = NewStore();
        await store.UpdateAsync(Alice, s => s with { Language = "ru" }, Token);
        var file = Directory.GetFiles(directory, "*.json").Single();
        await File.WriteAllTextAsync(file, "{\"density\":\"Compact\",\"somethingFromTheFuture\":{\"a\":1}}", Token);

        var settings = await store.GetAsync(Alice, Token);

        Assert.Equal(ListDensity.Compact, settings.Density);
        Assert.Null(settings.Language);
        Assert.Empty(settings.LabelColors);
    }

    [Fact]
    public async Task Delete_Removes_Everything_Kept_For_The_User_And_Is_Harmless_Twice()
    {
        var store = NewStore();
        await store.UpdateAsync(Alice, s => s with { Density = ListDensity.Compact }, Token);

        await store.DeleteAsync(Alice, Token);
        await store.DeleteAsync(Alice, Token);

        Assert.Empty(Files());
        Assert.Equal(ListDensity.Comfortable, (await store.GetAsync(Alice, Token)).Density);
    }

    [Fact]
    public async Task A_Mailbox_Is_Required()
    {
        var store = NewStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(" ", Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.UpdateAsync(Alice, null!, Token));
    }

    [Fact]
    public void The_Settings_Directory_Is_Made_When_It_Does_Not_Exist_And_Private_On_Unix()
    {
        NewStore();

        Assert.True(Directory.Exists(directory));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }
    }
}
