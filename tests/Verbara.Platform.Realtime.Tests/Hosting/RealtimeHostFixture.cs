using System.Security.Claims;
using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Verbara.Platform.Identity.Auth.Jwt;
using Verbara.Platform.Realtime.Clients;
using Verbara.Sdk.Pro.Push.SignalR.Events;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Verbara.Platform.Realtime.Tests.Hosting;

/// <summary>
/// The real Verbara.Platform.Realtime host — its own <c>Program</c>, not a hand-assembled copy —
/// booted once per test class. Program.cs runs the cluster-lock migration before it serves, so the
/// host gets a Testcontainers Postgres. Only two seams are replaced: the Platform.Api
/// account-status lookup (a switchable stub; the test chooses the verdict) and the JWT key pool,
/// seeded with a key the fixture signs with.
/// </summary>
public sealed class RealtimeHostFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string Issuer = "verbara-platform";

    private IContainer? _postgres;
    private SigningCredentials? _signing;

    /// <summary>The verdict the host's account-status lookup returns.</summary>
    internal SwitchableStatusClient Status { get; } = new();

    private string ConnectionString =>
        $"Host={_postgres!.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
        "Database=postgres;Username=postgres;Password=postgres";

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:16-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP: the image's entrypoint first runs a socket-only
            // temporary server, so a socket probe greens before 5432 accepts connections.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
        await WaitUntilConnectableAsync();

        _ = Server; // boots the host: migration, cluster leader election, hosted services

        var key = new JwtKeyEntry
        {
            KeyId = "realtime-host-test",
            Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            Algorithm = JwtKeyAlgorithm.Hs256,
            ActivatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            IsActive = true,
        };
        await Services.GetRequiredService<IJwtKeyStore>().UpsertAsync(key);
        _signing = new SigningCredentials(
            new SymmetricSecurityKey(Convert.FromBase64String(key.Key)) { KeyId = key.KeyId },
            SecurityAlgorithms.HmacSha256);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        // The host first, while its database is still there; then the container.
        await DisposeAsync();
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    /// <summary>
    /// A SignalR client for <c>/hubs/platform</c> carrying an access token shaped like the ones
    /// Platform.Api mints (<c>sub</c>, <c>tid</c>, <c>role</c>, issuer/audience), signed with the
    /// pool key the host validates against.
    /// </summary>
    internal HubConnection Connect(string tenantId, string userId, DateTimeOffset expiresAt)
    {
        var token = MintAccessToken(tenantId, userId, expiresAt);
        var server = Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/platform"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
    }

    /// <summary>
    /// A SignalR client for <c>/hubs/platform</c> over WebSockets, the transport the Web client
    /// uses: authenticated once, at the upgrade, so nothing in the transport re-checks the token
    /// afterwards. It presents whatever <paramref name="accessToken"/> returns each time it
    /// (re)connects, as the Web client's token factory does.
    /// </summary>
    internal HubConnection ConnectOverWebSockets(Func<string> accessToken, bool reconnect = false)
    {
        var server = Server;
        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/platform"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken());
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    var bearer = accessToken();
                    client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {bearer}";
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            });
        if (reconnect)
            builder.WithAutomaticReconnect();
        return builder.Build();
    }

    /// <summary>Publishes on the host's own push bus, as an Api replica's event arrives on a pod.</summary>
    internal ValueTask PublishAsync<TEvent>(TEvent pushEvent)
        where TEvent : PushEvent =>
        Services.GetRequiredService<IPushEventBus>().PublishAsync(pushEvent);

    /// <summary>
    /// Watches for <c>PlatformHub</c> announcing <paramref name="agentId"/>'s presence, which it does
    /// from its <c>OnConnectedAsync</c> — after the account-status filter admitted and registered the
    /// connection. Start watching before connecting.
    /// </summary>
    internal PresenceWatch WatchPresence(string agentId) =>
        new(Services.GetRequiredService<IPushEventBus>(), agentId);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Host configuration reaches the entry point as arguments, so Program.cs sees it while
        // composing (it reads ConnectionStrings:Cluster before Build()).
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cluster"] = ConnectionString,
        }));
        builder.ConfigureServices(services => services.AddSingleton<IUserAccessStatusClient>(Status));
        return base.CreateHost(builder);
    }

    /// <summary>
    /// An access token shaped like the ones Platform.Api mints, signed with the pool key the host
    /// validates against, expiring at <paramref name="expiresAt"/> (which may already have passed).
    /// </summary>
    internal string MintAccessToken(string tenantId, string userId, DateTimeOffset expiresAt)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", userId),
                new Claim("tid", tenantId),
                new Claim("role", "Agent"),
            ]),
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _signing ?? throw new InvalidOperationException("The host is not started."),
        });
    }

    // pg_isready can green a moment before the server takes real connections under host contention:
    // retry the first one so the host's startup migration meets a genuinely connectable server.
    private async Task WaitUntilConnectableAsync()
    {
        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)); // fence-allow: GUARD-TIMEOUT — backoff between connect retries while the Postgres container comes up
            }
        }
    }

    /// <summary>A pending "the hub announced this agent" signal; dispose to stop watching.</summary>
    internal sealed class PresenceWatch : IDisposable
    {
        private readonly TaskCompletionSource _announced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IDisposable _subscription;

        public PresenceWatch(IPushEventBus bus, string agentId) =>
            _subscription = bus.OfType<PresenceSnapshotEvent>().Subscribe(snapshot =>
            {
                if (snapshot.AgentId == agentId && !snapshot.IsRemove)
                    _announced.TrySetResult();
            });

        public Task Announced => _announced.Task;

        public void Dispose() => _subscription.Dispose();
    }

    /// <summary>The Platform.Api account-status lookup, answering whatever the test sets.</summary>
    internal sealed class SwitchableStatusClient : IUserAccessStatusClient
    {
        public UserAccessVerdict Verdict { get; set; } = UserAccessVerdict.Allowed;

        public Task<UserAccessVerdict> CheckAsync(string tenantId, string userId, CancellationToken cancellationToken) =>
            Task.FromResult(Verdict);
    }
}
