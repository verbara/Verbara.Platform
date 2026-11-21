using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Verbara.Platform.E2E.Harness.Hub;

/// <summary>
/// One <c>OnConversationStateChanged</c> frame as a client received it: the conversation id, the previous and new
/// state read from the JSON payload, and the client-side receive time.
/// </summary>
internal sealed record ConversationFrame(
    string ConversationId,
    string PreviousState,
    string NewState,
    DateTimeOffset ReceivedAt);

/// <summary>
/// One <c>OnClusterNodeStateChanged</c> frame as a client received it: the payload kept as raw JSON (no Pro type is
/// bound) and the client-side receive time (platform-drops-the-cluster-bridge, tasks 1.1 and 4.1).
/// </summary>
internal sealed record ClusterFrame(string PayloadJson, DateTimeOffset ReceivedAt);

/// <summary>
/// Pool of <see cref="HubConnection"/>s connected to
/// <c>/hubs/platform</c> via the cluster Gateway. Each client increments
/// its own <c>OnAgentStateChanged</c> receive counter so the scenario can
/// assert per-client receive parity (every connected client must observe
/// the same fanout count — exactly once per emitted event), and records every
/// <c>OnConversationStateChanged</c> and <c>OnClusterNodeStateChanged</c> frame it receives, in arrival order.
/// </summary>
/// <remarks>
/// <para>
/// The hub JWT is passed via <c>AccessTokenProvider</c> which sets the
/// query-string <c>?access_token=</c> on the WebSocket handshake (browsers
/// can't set headers on a WS upgrade; the Realtime
/// <c>JwtValidationConfigurator.IsQueryTokenPathAllowed</c> permits this
/// only on <c>/hubs/*</c>).
/// </para>
/// <para>
/// We do NOT bind to typed <c>AgentStatePayload</c> / <c>ConversationStatePayload</c>
/// records (defined in the SDK Pro.Push.SignalR package) — the harness avoids the Pro NuGet
/// dependency: agent frames are only counted, and conversation frames are read from the
/// JSON payload by property name, ignoring case.
/// </para>
/// </remarks>
internal sealed class SignalRClientPool : IAsyncDisposable
{
    private readonly List<HubConnection> _connections = new();
    private readonly string _hubUrl;
    private readonly string _accessToken;
    private int[] _receivedCounts = Array.Empty<int>();
    private ConcurrentQueue<ConversationFrame>[] _conversationFrames = Array.Empty<ConcurrentQueue<ConversationFrame>>();
    private ConcurrentQueue<ClusterFrame>[] _clusterFrames = Array.Empty<ConcurrentQueue<ClusterFrame>>();

    public SignalRClientPool(string hubUrl, string accessToken)
    {
        _hubUrl = hubUrl;
        _accessToken = accessToken;
    }

    public int Count => _connections.Count;

    public IReadOnlyList<int> ReceivedCounts => _receivedCounts;

    /// <summary>Each client's <see cref="HubConnectionState"/> as read now (a snapshot), in connection order.</summary>
    public IReadOnlyList<HubConnectionState> ConnectionStates => _connections.Select(c => c.State).ToList();

    /// <summary>The conversation frames each client holds, in arrival order (a snapshot).</summary>
    public IReadOnlyList<IReadOnlyList<ConversationFrame>> ConversationFrames =>
        _conversationFrames.Select(q => (IReadOnlyList<ConversationFrame>)q.ToArray()).ToList();

    /// <summary>The cluster node frames each client holds, in arrival order (a snapshot); never reset.</summary>
    public IReadOnlyList<IReadOnlyList<ClusterFrame>> ClusterFrames =>
        _clusterFrames.Select(q => (IReadOnlyList<ClusterFrame>)q.ToArray()).ToList();

    /// <summary>
    /// Drops every conversation frame recorded so far, so a window starts empty. The agent counters are left as
    /// they are.
    /// </summary>
    public void ResetConversationFrames()
    {
        foreach (var q in _conversationFrames)
        {
            q.Clear();
        }
    }

    /// <summary>Sets every client's agent-frame counter back to 0, so a window counts only its own frames.</summary>
    public void ResetAgentCounts()
    {
        for (var i = 0; i < _receivedCounts.Length; i++)
        {
            Interlocked.Exchange(ref _receivedCounts[i], 0);
        }
    }

    public async Task ConnectAsync(int count, CancellationToken ct)
    {
        _receivedCounts = new int[count];
        _conversationFrames = Enumerable.Range(0, count).Select(_ => new ConcurrentQueue<ConversationFrame>()).ToArray();
        _clusterFrames = Enumerable.Range(0, count).Select(_ => new ConcurrentQueue<ClusterFrame>()).ToArray();
        for (var i = 0; i < count; i++)
        {
            var clientIndex = i;

            var connection = new HubConnectionBuilder()
                .WithUrl(_hubUrl, options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(_accessToken);
                })
                .WithAutomaticReconnect()
                .Build();

            // The hub method name MUST match the Pro.Push.SignalR
            // IPlatformHubClient.OnAgentStateChanged signature — the
            // payload shape is irrelevant; we only count receives.
            connection.On<object>("OnAgentStateChanged", _ =>
            {
                Interlocked.Increment(ref _receivedCounts[clientIndex]);
                return Task.CompletedTask;
            });

            // IPlatformHubClient.OnConversationStateChanged: keep (conversation id, previous state, new state).
            connection.On<JsonElement>("OnConversationStateChanged", payload =>
            {
                _conversationFrames[clientIndex].Enqueue(new ConversationFrame(
                    ConversationId: ReadString(payload, "conversationId"),
                    PreviousState: ReadString(payload, "previousState"),
                    NewState: ReadString(payload, "newState"),
                    ReceivedAt: DateTimeOffset.UtcNow));
                return Task.CompletedTask;
            });

            // IPlatformHubClient.OnClusterNodeStateChanged (platform-drops-the-cluster-bridge): the method name as a
            // string and the payload as raw JSON, so the harness observes the frame without a Pro type.
            connection.On<JsonElement>("OnClusterNodeStateChanged", payload =>
            {
                _clusterFrames[clientIndex].Enqueue(new ClusterFrame(payload.GetRawText(), DateTimeOffset.UtcNow));
                return Task.CompletedTask;
            });

            await connection.StartAsync(ct).ConfigureAwait(false);
            _connections.Add(connection);
        }
    }

    private static string ReadString(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ValueKind == JsonValueKind.Null ? string.Empty : property.Value.GetRawText();
            }
        }

        return string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Swallow during teardown — the scenario already collected counts.
            }
        }
        _connections.Clear();
    }
}
