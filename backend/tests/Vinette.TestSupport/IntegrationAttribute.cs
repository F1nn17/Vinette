using Xunit.v3;

namespace Vinette.TestSupport;

/// <summary>
/// Marks tests that need Docker (a real mail server in Testcontainers). They carry the trait
/// Category=Integration: <c>make test</c> skips them, <c>make test-integration</c> runs only them.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class IntegrationAttribute : Attribute, ITraitAttribute
{
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
        [new KeyValuePair<string, string>("Category", "Integration")];
}
