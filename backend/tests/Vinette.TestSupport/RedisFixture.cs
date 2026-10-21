using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Vinette.TestSupport;

/// <summary>
/// A real Redis in a container. Share it between test classes with a collection fixture
/// (see <see cref="CollectionName"/>). The image is the last Redis under the BSD license.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    public const string CollectionName = "Redis";
    public const string Image = "redis:7.2-alpine";

    private const int RedisPort = 6379;

    private IContainer? container;

    /// <summary>Connection string for StackExchange.Redis.</summary>
    public string ConnectionString => $"{Container.Hostname}:{Container.GetMappedPublicPort(RedisPort)}";

    private IContainer Container => container ?? throw new InvalidOperationException("The Redis container is not started.");

    public async ValueTask InitializeAsync()
    {
        container = new ContainerBuilder(Image)
            .WithPortBinding(RedisPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();

        await container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}
