using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary><see cref="ContactResolverWriteBackContract"/> against <see cref="PostgresContactStore"/> on a real Postgres.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresContactResolverWriteBackTests : ContactResolverWriteBackContract, IClassFixture<ContactStoreFixture>
{
    public PostgresContactResolverWriteBackTests(ContactStoreFixture fixture) =>
        Store = new PostgresContactStore(fixture.DataSource);

    protected override IContactStore Store { get; }

    protected override TenantId Tenant { get; } = new($"t-{Guid.NewGuid():N}");
}
