// TokenHolder — thread-safe mutable Bearer token cache for long-running soaks.
// Used by scenarios that periodically refresh the JWT via a background task
// (R5.5 Phase D-LK fix, 2026-05-17). Reading is lock-free via a volatile
// reference swap; writes use a plain assignment (atomic for reference types
// on .NET).

namespace Verbara.Platform.LoadTests.Scenarios;

internal sealed class TokenHolder
{
    private volatile string _token;

    public TokenHolder(string initial) => _token = initial ?? "";

    public string Get() => _token;

    public void Set(string newToken) => _token = newToken ?? "";
}
