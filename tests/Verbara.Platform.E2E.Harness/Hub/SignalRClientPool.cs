using Microsoft.AspNetCore.SignalR.Client;

namespace Verbara.Platform.E2E.Harness.Hub;

/// <summary>
/// Pool of <see cref="HubConnection"/>s connected to
/// <c>/hubs/platform</c> via the cluster Gateway. Each client increments
/// its own <c>OnAgentStateChanged</c> receive counter so the scenario can
/// assert per-client receive parity (every connected client must observe
/// the same fanout count — exactly once per emitted event).
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
/// We do NOT bind to typed <c>AgentStatePayload</c> records (defined in
/// the SDK Pro.Push.SignalR package) — the harness avoids the Pro NuGet
/// dependency by handling the message as an opaque <see cref="object"/>;
/// only the receive count matters for the leader-gate assertion.
/// </para>
/// </remarks>
internal sealed class SignalRClientPool : IAsyncDisposable
{
    private readonly List<HubConnection> _connections = new();
    private readonly string _hubUrl;
    private readonly string _accessToken;
    private int[] _receivedCounts = Array.Empty<int>();

    public SignalRClientPool(string hubUrl, string accessToken)
    {
        _hubUrl = hubUrl;
        _accessToken = accessToken;
    }

    public int Count => _connections.Count;

    public IReadOnlyList<int> ReceivedCounts => _receivedCounts;

    public async Task ConnectAsync(int count, CancellationToken ct)
    {
        _receivedCounts = new int[count];
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

            await connection.StartAsync(ct).ConfigureAwait(false);
            _connections.Add(connection);
        }
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
