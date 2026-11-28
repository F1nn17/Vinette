using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Configuration;

namespace Vinette.Api.Tests;

/// <summary>The data directories are checked at start (VIN-142).</summary>
public sealed class DataDirectoriesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vinette-dirs-" + Guid.NewGuid().ToString("N"));

    public DataDirectoriesTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>A path that can never be a directory: it lies under a file.</summary>
    private string Impossible()
    {
        var file = Path.Combine(root, "a-file");
        File.WriteAllText(file, "x");
        return Path.Combine(file, "keys");
    }

    [Fact]
    public void A_Missing_Directory_Is_Made_And_Left_Clean()
    {
        var path = Path.Combine(root, "new", "deeper");

        DataDirectories.EnsureWritable("Settings:Path", path);

        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(path)); // the test file is removed again
    }

    [Fact]
    public void An_Existing_Directory_With_Files_Is_Left_As_It_Is()
    {
        var path = Path.Combine(root, "kept");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "key.xml"), "<key/>");

        DataDirectories.EnsureWritable("DataProtection:KeysPath", path);

        Assert.Equal(["key.xml"], Directory.GetFiles(path).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void A_Directory_That_Cannot_Be_Used_Is_Refused_With_The_Name_Of_The_Setting_And_What_To_Do()
    {
        var problem = Assert.Throws<DataDirectories.DataDirectoryException>(() => DataDirectories.EnsureWritable("DataProtection:KeysPath", Impossible()));

        Assert.StartsWith("DataProtection:KeysPath:", problem.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be created", problem.Message, StringComparison.Ordinal);
        Assert.Contains("/data", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_The_Settings_That_Are_Set_Are_Looked_At_And_The_Others_Are_Skipped()
    {
        var configured = DataDirectories.Configured(
            new KeyStorageOptions { KeysPath = "/k" },
            new SettingsOptions { Path = null },
            new AttachmentsOptions { Path = "  " },
            new SchedulingOptions { Path = "/s" }).ToList();

        Assert.Equal([("DataProtection:KeysPath", "/k"), ("Scheduling:Path", "/s")], configured);
    }

    [Fact]
    public void The_Temporary_Directory_Is_Looked_At_Only_When_The_Environment_Names_One_And_Is_Made_When_Missing()
    {
        Assert.Empty(DataDirectories.Temporary(null));
        Assert.Empty(DataDirectories.Temporary("  "));

        var tmp = Path.Combine(root, "data", "tmp");
        DataDirectories.EnsureWritable(DataDirectories.Temporary(tmp));

        Assert.True(Directory.Exists(tmp));
    }

    [Fact]
    public void The_First_Bad_Directory_Stops_The_Check_And_The_Good_Ones_Before_It_Are_Made()
    {
        var good = Path.Combine(root, "good");

        var problem = Assert.Throws<DataDirectories.DataDirectoryException>(() => DataDirectories.EnsureWritable(
            [("Settings:Path", good), ("Attachments:Path", Impossible())]));

        Assert.StartsWith("Attachments:Path:", problem.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(good));
    }

    [Fact]
    public async Task The_Application_Does_Not_Start_With_A_Directory_It_Cannot_Use_And_Starts_With_Good_Ones()
    {
        using var good = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DataProtection:KeysPath", Path.Combine(root, "keys"));
            b.UseSetting("Settings:Path", Path.Combine(root, "settings"));
            b.UseSetting("Attachments:Path", Path.Combine(root, "attachments"));
            b.UseSetting("Scheduling:Path", Path.Combine(root, "scheduled"));
        });
        using var client = good.CreateClient();
        var health = await client.GetAsync("/api/v1/health", TestContext.Current.CancellationToken);
        Assert.True(health.IsSuccessStatusCode);
        Assert.True(Directory.Exists(Path.Combine(root, "scheduled"))); // made at start

        using var bad = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Settings:Path", Impossible()));
        Assert.ThrowsAny<Exception>(() => bad.CreateClient());
    }
}
