using Verbara.Sdk;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Platform-owned <see cref="IAmiConnection"/> that defers resolution of the primary server's live
/// connection to FIRST USE, so the host always boots even with no telephony configured
/// (csat-completion, Platform/ADR-0020). The Pro voice CSAT adapter takes an <see cref="IAmiConnection"/>
/// by constructor injection; the <c>CsatRunnerOrchestrator</c> BackgroundService constructs every channel
/// adapter — voice included — during <c>Host.StartAsync</c>. A factory that eagerly resolved the primary
/// <c>VerbaraServer.Connection</c> and threw when none was configured killed every headless / no-telephony
/// boot (the CI OpenAPI-export capture, minimal deploys). This wrapper is fail-at-use, not fail-at-boot:
/// it holds the <see cref="VerbaraServerPool"/> and looks up <c>GetServer("primary")</c> on every member
/// access, throwing the descriptive <see cref="InvalidOperationException"/> only when a voice CSAT dispatch
/// genuinely needs AMI and no primary server exists.
/// </summary>
/// <remarks>
/// Mirrors <see cref="AmiDtmfSource"/>'s fail-closed access to the pool (both reach the primary connection via
/// <see cref="VerbaraServerPool.GetServer(string)"/>, the same accessor <c>VoiceConversationBridge</c> and
/// <c>VoiceCallControlService</c> use) — no parallel access path. Native AOT clean (no reflection): every
/// member is a direct virtual dispatch onto the resolved <see cref="IAmiConnection"/>. Voice CSAT dispatch
/// only ever fires for voice conversations, which require a live AMI connection, so deferring the failure to
/// the dispatch call site is semantically correct: an operator running the AMI-owner pod has a primary server;
/// a headless host never reaches a dispatch and so never trips the throw.
/// </remarks>
internal sealed class DeferredPrimaryAmiConnection : IAmiConnection
{
    private const string PrimaryServerId = "primary";

    private readonly VerbaraServerPool _serverPool;

    public DeferredPrimaryAmiConnection(VerbaraServerPool serverPool)
    {
        ArgumentNullException.ThrowIfNull(serverPool);
        _serverPool = serverPool;
    }

    // Resolves the primary server's live connection ON DEMAND. Throws only here — never at construction —
    // so the orchestrator (and thus the host) constructs cleanly with no telephony configured.
    private IAmiConnection Primary =>
        _serverPool.GetServer(PrimaryServerId)?.Connection
        ?? throw new InvalidOperationException("No primary AMI server is configured for voice CSAT dispatch.");

    public AmiConnectionState State => Primary.State;

    public string? AsteriskVersion => Primary.AsteriskVersion;

    public event Func<ManagerEvent, ValueTask>? OnEvent
    {
        add => Primary.OnEvent += value;
        remove => Primary.OnEvent -= value;
    }

    public event Action? Reconnected
    {
        add => Primary.Reconnected += value;
        remove => Primary.Reconnected -= value;
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        => Primary.ConnectAsync(cancellationToken);

    public ValueTask<ManagerResponse> SendActionAsync(ManagerAction action, CancellationToken cancellationToken = default)
        => Primary.SendActionAsync(action, cancellationToken);

    public ValueTask<TResponse> SendActionAsync<TResponse>(ManagerAction action, CancellationToken cancellationToken = default)
        where TResponse : ManagerResponse
        => Primary.SendActionAsync<TResponse>(action, cancellationToken);

    public IAsyncEnumerable<ManagerEvent> SendEventGeneratingActionAsync(
        ManagerAction action, CancellationToken cancellationToken = default)
        => Primary.SendEventGeneratingActionAsync(action, cancellationToken);

    public IDisposable Subscribe(IObserver<ManagerEvent> observer)
        => Primary.Subscribe(observer);

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        => Primary.DisconnectAsync(cancellationToken);

    // Disposal is a no-op: this wrapper owns no connection — the pooled VerbaraServer owns the real one and
    // disposes it. Resolving Primary here would throw when no telephony is configured (host shutdown path).
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
