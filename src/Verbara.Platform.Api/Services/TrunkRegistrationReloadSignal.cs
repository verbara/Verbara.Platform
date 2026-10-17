namespace Verbara.Platform.Api.Services;

/// <summary>
/// #385 — the in-process "trunk registrations changed" flag between a trunk write and
/// <see cref="TrunkRegistrationReloadWorker"/>. Asterisk loads <c>ps_registrations</c> rows only when
/// <c>res_pjsip_outbound_registration</c> loads or reloads, so a registration written after boot sends no
/// REGISTER until the module is reloaded; this flag asks for that reload.
/// </summary>
/// <remarks>
/// <see cref="RequestReload"/> is synchronous, never throws and never touches AMI: it only sets the dirty
/// flag and wakes the worker. Any number of requests before the worker runs collapse into one reload.
/// </remarks>
internal sealed class TrunkRegistrationReloadSignal
{
    private int _dirty;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True while a requested reload has not yet been accepted by Asterisk.</summary>
    public bool IsDirty => Volatile.Read(ref _dirty) == 1;

    /// <summary>Marks the registrations dirty and wakes the worker.</summary>
    public void RequestReload()
    {
        // Dirty first, then wake: a worker woken by the old task always sees the flag.
        Volatile.Write(ref _dirty, 1);
        Interlocked.Exchange(
            ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>Completes on the next <see cref="RequestReload"/> after it was read.</summary>
    internal Task WakeTask => Volatile.Read(ref _wake).Task;

    /// <summary>Clears the flag and answers whether it was set (the worker owns the reload from here).</summary>
    internal bool TryTake() => Interlocked.Exchange(ref _dirty, 0) == 1;

    /// <summary>Sets the flag again after a failed reload, without waking the worker.</summary>
    internal void Restore() => Volatile.Write(ref _dirty, 1);
}
