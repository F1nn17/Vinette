using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>One Dovecot container shared by all integration test classes of this assembly.</summary>
[CollectionDefinition(DovecotFixture.CollectionName)]
public sealed class DovecotCollectionDefinition : ICollectionFixture<DovecotFixture>;
