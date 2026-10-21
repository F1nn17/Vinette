using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>One Redis container shared by the Redis integration test classes of this assembly.</summary>
[CollectionDefinition(RedisFixture.CollectionName)]
public sealed class RedisCollectionDefinition : ICollectionFixture<RedisFixture>;
