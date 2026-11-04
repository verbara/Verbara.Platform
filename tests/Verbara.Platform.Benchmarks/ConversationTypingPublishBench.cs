// Verbara.Platform.Benchmarks — contact-history-opens-by-assignment task 3.6, cell T-3 (frozen plan:
// Predictions/contact-history-opens-by-assignment.md, sha256 a761a6da…; verbara-meta/ADR-0030 M-cost).
//
// One operation is one WebChat typing frame's publish, as WebChatEndpoints handles it. TYPING_MODE in the environment
// selects the variant, so A and B run the same binary in separate processes, interleaved A/B/A/B:
//   A       main's typing branch: publish the conversation.message typing frame, nothing else;
//   B       the candidate's: ConversationAudienceRead.CurrentAsync (GetByIdAsync on the Live-DB Postgres, internal to the
//           Api, bound once to a delegate), then the publish with that audience;
//   control as B, reading the conversation twice, which must show about twice B's added bytes.
// The seeded conversation is Active and owned by an agent, so the audience read allocates what a real frame's does.
//
// Environment: CLAIM_COST_PG, as ConversationClaimCostBench (GlobalSetup recreates the database "typing_cost").
//
// Repro: TYPING_MODE=B dotnet run -c Release --project tests/Verbara.Platform.Benchmarks -- --filter '*ConversationTypingPublishBench*'

using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres;

namespace Verbara.Platform.Benchmarks;

[MemoryDiagnoser]
public class ConversationTypingPublishBench : IDisposable
{
    private const string DatabaseName = "typing_cost";

    private static readonly TenantId Tenant = new("bench-tenant");
    private static readonly EntityId ConversationId = EntityId.From("conv-typing");

    private readonly PlatformEventBus _bus = new();
    private readonly ILoggerFactory _loggers = NullLoggerFactory.Instance;
    private NpgsqlDataSource? _dataSource;
    private ServiceProvider? _provider;
    private IConversationStore _store = null!;
    private Func<IConversationStore, ILoggerFactory, TenantId, EntityId, CancellationToken, Task<ConversationAudience>> _currentAudience = null!;
    private string _mode = "A";

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _mode = Environment.GetEnvironmentVariable("TYPING_MODE") ?? "A";
        if (_mode is not ("A" or "B" or "control"))
            throw new InvalidOperationException($"TYPING_MODE must be A, B or control, not {_mode}.");
        Console.WriteLine($"// TYPING_MODE={_mode}");

        var adminConnection = Environment.GetEnvironmentVariable("CLAIM_COST_PG")
            ?? throw new InvalidOperationException("Set CLAIM_COST_PG to the Live-DB container's connection string.");
        await using (var admin = NpgsqlDataSource.Create(adminConnection))
        {
            await using (var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)"))
                await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
            await using (var create = admin.CreateCommand($"CREATE DATABASE {DatabaseName}"))
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        _dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(adminConnection) { Database = DatabaseName }.ConnectionString);
        await ConversationClaimCostBench.ApplyMigrationsAsync(_dataSource).ConfigureAwait(false);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddPostgresStorage(_dataSource);
        _provider = services.BuildServiceProvider();
        _store = _provider.GetRequiredService<IConversationStore>();
        await _store.SaveAsync(new Conversation
        {
            ConversationId = ConversationId,
            TenantId = Tenant,
            ContactId = EntityId.From("contact-typing"),
            Channel = ChannelType.WebChat,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(EntityId.From("agent-typing")),
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).ConfigureAwait(false);

        var read = typeof(Verbara.Platform.Api.Services.ConversationCloseMetrics).Assembly
            .GetType("Verbara.Platform.Api.Endpoints.Shared.ConversationAudienceRead", throwOnError: true)!;
        _currentAudience = read.GetMethod("CurrentAsync")!
            .CreateDelegate<Func<IConversationStore, ILoggerFactory, TenantId, EntityId, CancellationToken, Task<ConversationAudience>>>();

        // The instrument checks itself: B reads the seeded owner, so the frame carries a real audience.
        var audience = await _currentAudience(_store, _loggers, Tenant, ConversationId, CancellationToken.None).ConfigureAwait(false);
        if (audience.OwnerAgentId != "agent-typing")
            throw new InvalidOperationException($"The audience read gave {audience}, not the seeded owner.");
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync().ConfigureAwait(false);
        if (_dataSource is not null)
            await _dataSource.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public async Task Typing()
    {
        switch (_mode)
        {
            case "A":
                _bus.Publish(new ConversationMessageEvent(Tenant.Value, ConversationId.Value, "", "TypingIndicator", "WebChat"));
                return;
            case "B":
            {
                var audience = await _currentAudience(_store, _loggers, Tenant, ConversationId, CancellationToken.None).ConfigureAwait(false);
                _bus.Publish(new ConversationMessageEvent(Tenant.Value, ConversationId.Value, "", "TypingIndicator", "WebChat") { Audience = audience });
                return;
            }

            default:
            {
                await _currentAudience(_store, _loggers, Tenant, ConversationId, CancellationToken.None).ConfigureAwait(false);
                var audience = await _currentAudience(_store, _loggers, Tenant, ConversationId, CancellationToken.None).ConfigureAwait(false);
                _bus.Publish(new ConversationMessageEvent(Tenant.Value, ConversationId.Value, "", "TypingIndicator", "WebChat") { Audience = audience });
                return;
            }
        }
    }

    public void Dispose()
    {
        _bus.Dispose();
        GC.SuppressFinalize(this);
    }
}
