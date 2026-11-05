using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary><see cref="ContactIdentityKeyStoreContract"/> against <see cref="InMemoryContactStore"/>.</summary>
public sealed class InMemoryContactIdentityKeyStoreTests : ContactIdentityKeyStoreContract
{
    protected override IContactStore Store { get; } = new InMemoryContactStore();

    protected override TenantId Tenant { get; } = new($"t-{Guid.NewGuid():N}");
}
