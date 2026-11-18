namespace Vinette.Api.Tests;

/// <summary>
/// The OpenAPI document names a schema by the short name of its type, so two public types of the API with one name become one schema
/// (the later one silently replaces the earlier in the generated TypeScript). Found once, with two <c>AttachmentDto</c> types.
/// </summary>
public sealed class ContractNamesTests
{
    [Fact]
    public void No_Two_Public_Types_Of_The_Api_Share_A_Short_Name()
    {
        var clashes = typeof(Program).Assembly.GetExportedTypes()
            .Where(t => t.Name.EndsWith("Dto", StringComparison.Ordinal) || t.Name.EndsWith("Request", StringComparison.Ordinal) || t.Name.EndsWith("Response", StringComparison.Ordinal))
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => t.FullName))}")
            .ToList();

        Assert.True(clashes.Count == 0, "Types with one name become one schema in the API contract: " + string.Join("; ", clashes));
    }
}
