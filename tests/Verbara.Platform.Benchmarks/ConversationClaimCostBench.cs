// Verbara.Platform.Benchmarks — conversations-close-after-inactivity task 2.9: the per-message cost of the claim
// (verbara-meta/ADR-0030 §2, M-cost; design Architectural Risk "Hot-path latency").
//
// Two benchmarks, each one operation on one seeded Active WhatsApp conversation through the Postgres stores on a
// Live-DB container:
//   Inbound = InboundMessagePipeline.ProcessAsync of a fresh message (a new external id per op);
//   Reply   = IConversationService.SendMessageAsync (DefaultConversationService) with a fake connector that succeeds
//             without I/O.
// The class calls only APIs that exist at both commits, so the same file builds at the baseline (A, the merge base
// 3c3ed3d9, no claim) and at the candidate (B, this branch's head).
//
// PREDICTION (written before the run, committed before the run, not edited after it):
//   per operation, B allocates more than A by between 0.5 KiB and 4 KiB on each benchmark (one more NpgsqlCommand
//   with three to five parameters), and B's median time exceeds A's by one loopback round-trip, between 0.05 ms and
//   1 ms.
//
// FLIP RULE (bytes decide, time informs):
//   if, on either benchmark, BOTH B runs allocate more than A's larger run by more than 8 KiB or more than 25 %, the
//   claim's allocation is not what the design assumes: stop, record it, and return D7/D9 to review (the candidate fix
//   is folding the claim and the insert into one statement) before 3.6. A time delta beyond 2 ms on loopback is
//   recorded and raised to the operator, and does not by itself stop the change. A result inside the prediction is
//   recorded and changes nothing.
//
// CONTROL: built with CLAIM_COST_CONTROL=true in the environment (B only), the conversation store is wrapped so every claim UPDATE runs
// twice (ClaimCostControlStore.cs); the run must show the added bytes per op.
//
// Environment: CLAIM_COST_PG = a connection string to the Live-DB container's "postgres" database. GlobalSetup drops
// and recreates the database "claim_cost", applies the embedded migrations of the Storage.Postgres assembly under test
// in name order, and seeds one contact + one Active WhatsApp conversation with one inbound message (the open
// WhatsApp window), so every run starts from the same seeded database.
//
// Repro: dotnet run -c Release --project tests/Verbara.Platform.Benchmarks -- --filter '*ConversationClaimCostBench*'
//
// contact-history-opens-by-assignment task 3.6, cell T-1 (frozen plan: Predictions/contact-history-opens-by-assignment.md,
// sha256 a761a6da…): two more operations, built only on APIs that exist at the merge base 0cfdb299 and at the candidate:
//   InboundPublish = WebhookInboundProcessor.ProcessAsync of one fresh webhook message for the seeded Active
//                    conversation, the real inbound publishing path (internal to the Api, reached through a delegate
//                    bound once in GlobalSetup, so no reflection runs per op), with an in-process event bus;
//   Save           = IConversationStore.SaveAsync of the seeded conversation, the whole-row upsert with the D7 CASE.
// Its control is AUDIENCE_COST_CONTROL=true (B only): AudienceCostControlStore.cs doubles SaveAsync, and a local patch of
// the measured tree adds a 2 KiB buffer per ConversationOffer.AudienceOf call. The routing and bot seams are stubs that
// throw: the seeded conversation is Active and owned by no bot, so the processor never reaches them.

using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using System.Reflection;
using Verbara.Platform.Audit;
using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.Postgres;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Benchmarks;

[MemoryDiagnoser]
public class ConversationClaimCostBench
{
    private const string DatabaseName = "claim_cost";
    private const string MigrationPrefix = "Verbara.Platform.Storage.Postgres.Migrations.";
    private const string Phone = "+15550100001";

    private static readonly TenantId Tenant = new("bench-tenant");
    private static readonly ChannelAddress From = new(ChannelType.WhatsApp, Phone);
    private static readonly MessageEnvelope Text = new([new TextBlock("hello, is anyone there?")]);
    private static readonly EntityId SystemSender = EntityId.From("system");

    private ServiceProvider? _provider;
    private NpgsqlDataSource? _dataSource;
    private IInboundMessagePipeline _pipeline = null!;
    private IConversationService _conversations = null!;
    private IConversationStore _conversationStore = null!;
    private Func<TenantId, ChannelType, WebhookResult, CancellationToken, Task> _processWebhook = null!;
    private Conversation _seeded = null!;
    private EntityId _conversationId;
    private long _sequence;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var adminConnection = Environment.GetEnvironmentVariable("CLAIM_COST_PG")
            ?? throw new InvalidOperationException("Set CLAIM_COST_PG to the Live-DB container's connection string.");

        await using (var admin = NpgsqlDataSource.Create(adminConnection))
        {
            await using (var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)"))
                await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
            await using (var create = admin.CreateCommand($"CREATE DATABASE {DatabaseName}"))
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnection) { Database = DatabaseName };
        _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await ApplyMigrationsAsync(_dataSource).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new PlatformEventBus());
        services.AddPlatformConversations();
        services.AddPlatformChannels();
        Verbara.Platform.Switchboard.ServiceCollectionExtensions.AddSwitchboard(services);
        services.AddPostgresStorage(_dataSource);
        services.AddSingleton<IChannelConnector, SucceedingWhatsAppConnector>();
        services.AddSingleton<IAuditService, DefaultAuditService>();
        services.AddSingleton<Verbara.Platform.Api.Services.ConversationCloseMetrics>();
        services.AddSingleton<IInboundRouter, UnreachableRouter>();
        services.AddSingleton<IVirtualAgent, UnreachableVirtualAgent>();
        // The switchboard only routes (an Active conversation never is) and hands a bot's conversation back; the proxy
        // throws if either is ever reached. AddSwitchboard's own registration needs the capacity seam, not wired here.
        services.AddSingleton(_ => Unreachable.Of<IConversationSwitchboard>());
        var apiAssembly = typeof(Verbara.Platform.Api.Services.ConversationCloseMetrics).Assembly;
        var announcerType = apiAssembly.GetType("Verbara.Platform.Api.Services.CustomerReturnAnnouncer", throwOnError: true)!;
        services.AddSingleton(announcerType, sp => ActivatorUtilities.CreateInstance(sp, announcerType));
        ClaimCostControl.Apply(services);
        AudienceCostControl.Apply(services);
        _provider = services.BuildServiceProvider();

        _pipeline = _provider.GetRequiredService<IInboundMessagePipeline>();
        _conversations = _provider.GetRequiredService<IConversationService>();
        _conversationStore = _provider.GetRequiredService<IConversationStore>();
        var processorType = apiAssembly.GetType("Verbara.Platform.Api.Services.WebhookInboundProcessor", throwOnError: true)!;
        var processor = ActivatorUtilities.CreateInstance(_provider, processorType);
        _processWebhook = processorType.GetMethod("ProcessAsync", BindingFlags.Instance | BindingFlags.Public)!
            .CreateDelegate<Func<TenantId, ChannelType, WebhookResult, CancellationToken, Task>>(processor);

        // Seed: the first inbound creates the contact (with its WhatsApp address) and the conversation, and opens the
        // WhatsApp window; the row is then moved to Active.
        var first = await _pipeline.ProcessAsync(NextInbound(), Tenant, ChannelType.WhatsApp, CancellationToken.None)
            .ConfigureAwait(false);
        _conversationId = first.ConversationId;
        await using (var activate = _dataSource.CreateCommand(
                         "UPDATE conversations SET state = @State WHERE tenant_id = @TenantId AND conversation_id = @Id"))
        {
            activate.Parameters.Add(new NpgsqlParameter("State", NpgsqlDbType.Integer) { Value = (int)ConversationState.Active });
            activate.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = Tenant.Value });
            activate.Parameters.Add(new NpgsqlParameter("Id", NpgsqlDbType.Text) { Value = _conversationId.Value });
            if (await activate.ExecuteNonQueryAsync().ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The seeded conversation was not found.");
        }

        // The instrument checks itself: both operations take the path the benchmark claims to measure.
        var joined = await _pipeline.ProcessAsync(NextInbound(), Tenant, ChannelType.WhatsApp, CancellationToken.None)
            .ConfigureAwait(false);
        if (joined.IsNewConversation || joined.IsDuplicate || joined.ConversationId != _conversationId)
            throw new InvalidOperationException("Inbound did not join the seeded Active conversation.");
        var sent = await _conversations.SendMessageAsync(
                _conversationId, Tenant, Text, SystemSender, ConversationOwnerKind.System, CancellationToken.None)
            .ConfigureAwait(false);
        if (sent.DeliveryStatus != MessageDeliveryStatus.Sent)
            throw new InvalidOperationException($"Reply was not sent: {sent.DeliveryStatus}.");

        // InboundPublish must reach the publish of the inbound conversation.message of the seeded conversation.
        var published = 0;
        using (_provider.GetRequiredService<PlatformEventBus>().Events.Subscribe(new CountingObserver(
                   e => e is ConversationMessageEvent m && m.ConversationId == _conversationId.Value && m.Sender == "Inbound",
                   () => Interlocked.Increment(ref published))))
        {
            await InboundPublish().ConfigureAwait(false);
        }
        if (published != 1)
            throw new InvalidOperationException($"InboundPublish published {published} inbound messages, not 1.");

        _seeded = await _conversationStore.GetByIdAsync(Tenant, _conversationId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The seeded conversation could not be read back.");
        await Save().ConfigureAwait(false);
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
    public Task<PipelineResult> Inbound() =>
        _pipeline.ProcessAsync(NextInbound(), Tenant, ChannelType.WhatsApp, CancellationToken.None);

    [Benchmark]
    public Task<Message> Reply() =>
        _conversations.SendMessageAsync(
            _conversationId, Tenant, Text, SystemSender, ConversationOwnerKind.System, CancellationToken.None);

    [Benchmark]
    public Task InboundPublish() =>
        _processWebhook(Tenant, ChannelType.WhatsApp, WebhookResult.From([NextInbound()], []), CancellationToken.None);

    [Benchmark]
    public Task Save() => _conversationStore.SaveAsync(_seeded, CancellationToken.None);

    private InboundMessage NextInbound() =>
        new(From, Text, "wamid.in." + Interlocked.Increment(ref _sequence).ToString(System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.UtcNow);

    internal static async Task ApplyMigrationsAsync(NpgsqlDataSource dataSource)
    {
        var assembly = typeof(Verbara.Platform.Storage.Postgres.ServiceCollectionExtensions).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("No embedded migrations found.");

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync().ConfigureAwait(false);
            await using var cmd = dataSource.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private sealed class CountingObserver(Func<PlatformEvent, bool> match, Action hit) : IObserver<PlatformEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(PlatformEvent value)
        {
            if (match(value))
                hit();
        }
    }

    /// <summary>A proxy of <typeparamref name="T"/> whose every member throws: a seam the measured path never reaches.</summary>
#pragma warning disable CA1852 // DispatchProxy.Create derives a type from it at run time, so it cannot be sealed.
    internal class Unreachable : DispatchProxy
#pragma warning restore CA1852
    {
        public static T Of<T>()
            where T : class => Create<T, Unreachable>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"The benchmark reached {targetMethod?.Name}, which its seeded conversation never does.");
    }

    private sealed class UnreachableRouter : IInboundRouter
    {
        public Task<RouteResult> RouteAsync(RoutingContext context, CancellationToken ct) =>
            throw new InvalidOperationException("The benchmark's seeded conversation is never routed.");
    }

    private sealed class UnreachableVirtualAgent : IVirtualAgent
    {
        public Task<BotResponse> ProcessMessageAsync(EntityId conversationId, TenantId tenantId, MessageEnvelope message, CancellationToken ct) =>
            throw new InvalidOperationException("No bot owns the benchmark's seeded conversation.");
    }

    /// <summary>A WhatsApp connector that succeeds without I/O, with a new provider id per send.</summary>
    private sealed class SucceedingWhatsAppConnector : IChannelConnector
    {
        private long _sequence;

        public ChannelType Channel => ChannelType.WhatsApp;

        public Task<SendResult> SendAsync(OutboundMessage message, CancellationToken ct) =>
            Task.FromResult(new SendResult(
                true,
                "wamid.out." + Interlocked.Increment(ref _sequence).ToString(System.Globalization.CultureInfo.InvariantCulture),
                null,
                null));

        public Task<MessageDeliveryStatus?> GetStatusAsync(string externalMessageId, CancellationToken ct) =>
            Task.FromResult<MessageDeliveryStatus?>(null);
    }
}

/// <summary>The control hook: a no-op unless the project is built with <c>CLAIM_COST_CONTROL=true</c> in the environment.</summary>
internal static partial class ClaimCostControl
{
    public static void Apply(IServiceCollection services) => ApplyCore(services);

    static partial void ApplyCore(IServiceCollection services);
}

/// <summary>The T-1 control hook: a no-op unless the project is built with <c>AUDIENCE_COST_CONTROL=true</c> in the environment.</summary>
internal static partial class AudienceCostControl
{
    public static void Apply(IServiceCollection services) => ApplyCore(services);

    static partial void ApplyCore(IServiceCollection services);
}
