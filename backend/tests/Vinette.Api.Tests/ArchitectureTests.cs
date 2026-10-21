namespace Vinette.Api.Tests;

public sealed class ArchitectureTests
{
    private static readonly string[] ProtocolLibraries = ["MailKit", "MimeKit"];

    [Fact]
    public void Api_Does_Not_Reference_The_Mail_Protocol_Libraries()
    {
        var references = typeof(Program).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

        Assert.DoesNotContain(references, name => ProtocolLibraries.Any(p => name.StartsWith(p, StringComparison.Ordinal)));
    }

    [Fact]
    public void Api_Source_Does_Not_Use_The_Mail_Protocol_Libraries()
    {
        var root = FindApiSourceDirectory();

        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadLines(f).Any(l => ProtocolLibraries.Any(p => l.TrimStart().StartsWith($"using {p}", StringComparison.Ordinal))))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    private static string FindApiSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Vinette.Api");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("backend/src/Vinette.Api was not found above the test output directory.");
    }
}
