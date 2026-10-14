using Verbara.Platform.Identity.Mfa;

namespace Verbara.Platform.Identity.Tests.Mfa;

/// <summary>
/// The shared <see cref="IMfaPendingCache"/> contract (<see cref="MfaPendingCacheContractTests"/>) run against
/// <see cref="InMemoryMfaPendingCache"/>.
/// </summary>
public sealed class InMemoryMfaPendingCacheContractTests : MfaPendingCacheContractTests
{
    protected override IMfaPendingCache CreateCache() => new InMemoryMfaPendingCache();
}
