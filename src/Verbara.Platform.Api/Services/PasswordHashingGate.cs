using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Bounds how many password hashes (Argon2id, legacy BCrypt) one Api process computes at the same
/// time. A caller over the bound waits asynchronously for a slot, without holding a thread-pool
/// thread, and never fails for lack of one.
/// </summary>
/// <remarks>
/// <para>
/// A password hash is deliberately CPU- and memory-bound, and the library call is synchronous. Without
/// a bound, a burst of sign-ins that arrive together (every agent of a shift opening the console in the
/// same second) runs one hash per request on the thread pool at once: every pool thread is busy hashing,
/// the pool grows a thread at a time, and every other request on the replica — token refresh, the push
/// follower, health checks — queues behind the burst. With the gate, at most
/// <see cref="MaxConcurrency"/> hashes run; the rest are parked continuations that cost no thread.
/// </para>
/// <para>
/// The bound comes from <c>Identity:PasswordHashing:MaxConcurrency</c>. Unset, zero or negative means
/// <see cref="DefaultMaxConcurrency"/>: half the processors the process may use, at least one, which
/// leaves the other half to the rest of the request pipeline during a burst.
/// </para>
/// </remarks>
internal sealed class PasswordHashingGate : IDisposable
{
    /// <summary>Configuration key for the bound.</summary>
    public const string MaxConcurrencyKey = "Identity:PasswordHashing:MaxConcurrency";

    private static readonly Lazy<PasswordHashingGate> s_shared = new(() => new PasswordHashingGate(0));

    private readonly SemaphoreSlim _slots;

    /// <summary>A gate admitting <paramref name="maxConcurrency"/> hashes at once.</summary>
    /// <param name="maxConcurrency">Hashes allowed at once; zero or negative selects
    /// <see cref="DefaultMaxConcurrency"/>.</param>
    public PasswordHashingGate(int maxConcurrency)
    {
        MaxConcurrency = maxConcurrency > 0 ? maxConcurrency : DefaultMaxConcurrency;
        _slots = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
    }

    /// <summary>A gate sized from <see cref="MaxConcurrencyKey"/>; unset or not positive selects the default.</summary>
    public static PasswordHashingGate FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new PasswordHashingGate(configuration.GetValue<int?>(MaxConcurrencyKey) ?? 0);
    }

    /// <summary>Half of <see cref="Environment.ProcessorCount"/>, at least one.</summary>
    public static int DefaultMaxConcurrency => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>The bound in force.</summary>
    public int MaxConcurrency { get; }

    /// <summary>Slots free right now (for tests and diagnostics).</summary>
    internal int AvailableSlots => _slots.CurrentCount;

    /// <summary>
    /// The gate registered in the request's services, or a process-wide one with the default bound when
    /// none is registered (handler tests, tools), so no hashing path is ever unbounded.
    /// </summary>
    public static PasswordHashingGate For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RequestServices?.GetService<PasswordHashingGate>() ?? s_shared.Value;
    }

    /// <summary><see cref="PasswordService.VerifyPassword"/> inside the bound.</summary>
    public Task<bool> VerifyAsync(string password, string hash, CancellationToken ct) =>
        RunAsync(() => PasswordService.VerifyPassword(password, hash), ct);

    /// <summary><see cref="PasswordService.HashPassword"/> inside the bound.</summary>
    public Task<string> HashAsync(string password, CancellationToken ct) =>
        RunAsync(() => PasswordService.HashPassword(password), ct);

    /// <summary>
    /// Waits for a slot (honouring <paramref name="ct"/> while waiting), runs <paramref name="work"/>,
    /// and frees the slot whatever the outcome. A cancelled wait never takes a slot.
    /// </summary>
    internal async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return work();
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// Waits for a slot (honouring <paramref name="ct"/> while waiting) and holds it until the returned
    /// <see cref="Slot"/> is disposed. For a caller that must read or write state around its hash while no
    /// other hash of the same queue overtakes it — the sign-in re-reads the account's lock once it holds a
    /// slot and records a rejected password before it frees the slot. A cancelled wait never takes a slot.
    /// </summary>
    public async Task<Slot> EnterAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        return new Slot(_slots);
    }

    public void Dispose() => _slots.Dispose();

    /// <summary>A held hashing slot (<see cref="EnterAsync"/>); disposing it frees the slot, once.</summary>
    public sealed class Slot : IDisposable
    {
        private SemaphoreSlim? _slots;

        internal Slot(SemaphoreSlim slots) => _slots = slots;

        /// <summary><see cref="PasswordService.VerifyPassword"/> in this slot.</summary>
        public bool Verify(string password, string hash)
        {
            ObjectDisposedException.ThrowIf(_slots is null, this);
            return PasswordService.VerifyPassword(password, hash);
        }

        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
