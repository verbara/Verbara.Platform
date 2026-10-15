using Verbara.Platform.Identity.Mfa;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// In-memory <see cref="IRecoveryCodeCensus"/>: the same count as <c>PostgresRecoveryCodeCensus</c>, over
/// <see cref="InMemoryUserStore"/>'s users. Nothing is wrapped in memory, so no unwrapping is needed.
/// </summary>
internal sealed class InMemoryRecoveryCodeCensus(InMemoryUserStore store) : IRecoveryCodeCensus
{
    private readonly InMemoryUserStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<RecoveryCodeCensus> CountAsync(CancellationToken ct)
    {
        long onArgon2id = 0;
        long onLegacy = 0;

        foreach (var codes in _store.RecoveryCodeArrays())
        {
            ct.ThrowIfCancellationRequested();
            if (codes is not { Count: > 0 })
                continue;

            if (RecoveryCodeFamilies.HoldsLegacy(codes))
                onLegacy++;
            else
                onArgon2id++;
        }

        return Task.FromResult(new RecoveryCodeCensus(onArgon2id, onLegacy));
    }
}
