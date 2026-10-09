using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.Postgres.Seeds;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// The host for the conversation-ownership suites: <see cref="ImpersonationApiFactory"/>'s real
/// in-memory stores (users, agents, conversations, queues, audit) and real JWTs, with helpers that
/// seed what an ownership decision reads — users, the Agent profiles they hold, conversations and
/// their offers, queues — and read back what it wrote.
/// </summary>
/// <remarks>
/// <para>
/// Every user's server-side permissions are its role template's: the stubbed
/// <see cref="IUserRoleStore"/> looks the user up and answers with
/// <see cref="RoleTemplateSeeder.GetTemplatePermissions"/> for the template its role maps to, which is
/// what a seeded tenant gives. A principal that is not a user of the tenant (an API key with no user)
/// holds nothing. Impersonation tokens carry their own permissions and never reach the store.
/// </para>
/// <para>
/// An Agent profile's id is never its user's id, as in production (<c>POST /admin/agents</c> mints
/// one), so a check that compares an owner with the wrong identifier cannot pass by accident.
/// </para>
/// </remarks>
public class ConversationOwnershipApiFactory : ImpersonationApiFactory
{
    public static readonly TenantId Tenant = new(CustomerTenantId);

    /// <summary>The published typification schema's nodes: a root and its one leaf.</summary>
    public const string TypificationRootNodeId = "ownership-root";
    public const string TypificationLeafNodeId = "ownership-leaf";

    private readonly Lock _schemaLock = new();
    private bool _schemaSeeded;

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);

        services.RemoveAll<IUserRoleStore>();
        services.AddSingleton<IUserRoleStore>(RoleTemplateGrants);
    }

    private static IUserRoleStore RoleTemplateGrants(IServiceProvider services)
    {
        var roles = Substitute.For<IUserRoleStore>();
        roles.GetEffectivePermissionsAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(call => GrantsOfAsync(services, call.ArgAt<TenantId>(0), call.ArgAt<EntityId>(1)));
        return roles;
    }

    private static async Task<IReadOnlySet<string>> GrantsOfAsync(IServiceProvider services, TenantId tenantId, EntityId userId)
    {
        var user = await services.GetRequiredService<IUserStore>().GetByIdAsync(tenantId, userId, CancellationToken.None);
        if (user is null)
            return new HashSet<string>(StringComparer.Ordinal);

        var template = user.Role switch
        {
            UserRole.Agent => "agent",
            UserRole.Supervisor => "supervisor",
            UserRole.Admin => "admin",
            UserRole.Api => "api",
            _ => string.Empty,
        };
        return new HashSet<string>(RoleTemplateSeeder.GetTemplatePermissions(template), StringComparer.Ordinal);
    }

    // ─── People ───────────────────────────────────────────────────────────────

    /// <summary>A new Active user of the customer tenant with <paramref name="role"/> and no Agent profile.</summary>
    public User NewUser(UserRole role, string label) =>
        SaveUser($"{label}-{Guid.NewGuid():N}", CustomerTenantId, role, UserStatus.Active);

    /// <summary>A new Active user of the customer tenant with <paramref name="role"/> and an Agent profile.</summary>
    public (User User, Agent Agent) NewAgent(
        string label, UserRole role = UserRole.Agent, ChannelCapacityOverride? capacity = null)
    {
        var user = NewUser(role, label);
        return (user, AddAgentProfile(user, capacity));
    }

    /// <summary>Gives <paramref name="user"/> an Agent profile in the customer tenant.</summary>
    public Agent AddAgentProfile(User user, ChannelCapacityOverride? capacity = null)
    {
        var agent = new Agent
        {
            AgentId = EntityId.From($"agent-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            UserId = user.UserId,
            DisplayName = user.DisplayName,
            State = AgentState.Available,
            CapacityOverride = capacity ?? new ChannelCapacityOverride(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAgentStore>().SaveAsync(agent, CancellationToken.None)
            .GetAwaiter().GetResult();
        return agent;
    }

    /// <summary>A client that signs in as <paramref name="user"/> with an access token this host mints.</summary>
    public HttpClient ClientFor(User user) => CreateBearerClient(MintAccessToken(user));

    /// <summary>
    /// A client authenticated by a standard API key of the customer tenant that is bound to no user —
    /// a machine-to-machine key, with no role and no Agent profile.
    /// </summary>
    public HttpClient UnboundApiKeyClient()
    {
        var rawKey = $"test-unbound-key-{Guid.NewGuid():N}";
        using (var scope = Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IApiKeyStore>().SaveAsync(
                new ApiKey
                {
                    KeyId = EntityId.From($"test-unbound-key-id-{Guid.NewGuid():N}"),
                    TenantId = Tenant,
                    Name = "Unbound integration key",
                    HashedKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))),
                    Scopes = ["*"],
                    KeyType = ApiKeyType.Standard,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawKey);
        return client;
    }

    // ─── Work ─────────────────────────────────────────────────────────────────

    /// <summary>A conversation of the customer tenant in <paramref name="state"/>, owned by <paramref name="owner"/>.</summary>
    public Conversation SeedConversation(
        ConversationOwner? owner,
        ConversationState state,
        IReadOnlyDictionary<string, string>? metadata = null,
        ChannelType channel = ChannelType.WebChat)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = channel,
            State = state,
            Owner = owner,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        foreach (var (key, value) in metadata ?? new Dictionary<string, string>())
            conversation.SetMetadata(key, value);

        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None)
            .GetAwaiter().GetResult();
        return conversation;
    }

    /// <summary>
    /// A queued conversation offered to <paramref name="agent"/>, stamped the way an offer is
    /// (<c>_offeredTo</c>, <c>_offeredAt</c>).
    /// </summary>
    public Conversation SeedOfferTo(Agent agent, Queue queue) =>
        SeedConversation(
            ConversationOwner.ForQueue(queue.QueueId),
            ConversationState.Offered,
            new Dictionary<string, string>
            {
                ["_offeredTo"] = agent.AgentId.Value,
                ["_offeredAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });

    /// <summary>A queue of the customer tenant.</summary>
    public Queue SeedQueue()
    {
        var queue = new Queue
        {
            QueueId = EntityId.From($"queue-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            Name = $"Ownership queue {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IQueueStore>().SaveAsync(queue, CancellationToken.None)
            .GetAwaiter().GetResult();
        return queue;
    }

    /// <summary>The conversation as the store holds it now.</summary>
    public Conversation? Load(EntityId conversationId)
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IConversationStore>()
            .GetByIdAsync(Tenant, conversationId, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Every audit entry the customer tenant holds for <paramref name="conversationId"/>.</summary>
    public IReadOnlyList<AuditEntry> AuditOf(EntityId conversationId)
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IAuditStore>()
            .GetByEntityAsync(Tenant, "Conversation", conversationId.Value, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>The number of <paramref name="channel"/> conversations the capacity ledger counts for <paramref name="agentId"/>.</summary>
    public int LoadOf(EntityId agentId, ChannelType channel = ChannelType.WebChat) =>
        Services.GetRequiredService<IAgentCapacityService>()
            .GetCurrentLoadAsync(Tenant, agentId, channel, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Counts one <paramref name="channel"/> conversation against <paramref name="agentId"/>'s capacity.</summary>
    public void Reserve(EntityId agentId, ChannelType channel = ChannelType.WebChat) =>
        Services.GetRequiredService<IAgentCapacityService>()
            .ReserveAsync(Tenant, agentId, channel, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Binds one published typification schema to the whole customer tenant (once per host): a root
    /// node <see cref="TypificationRootNodeId"/> with one leaf <see cref="TypificationLeafNodeId"/>
    /// and no fields, so a submission of that path is valid.
    /// </summary>
    public void EnsureTypificationSchema()
    {
        lock (_schemaLock)
        {
            if (_schemaSeeded)
                return;

            var schemaId = EntityId.From($"schema-{Guid.NewGuid():N}");
            var schema = new TypificationSchema
            {
                SchemaId = schemaId,
                TenantId = Tenant,
                Name = "Ownership schema",
                Version = 1,
                IsPublished = true,
                MaxDepth = 5,
                Nodes =
                [
                    new TypificationNode
                    {
                        NodeId = EntityId.From(TypificationRootNodeId),
                        ParentNodeId = null,
                        Label = "Outcome",
                        Code = "OUTCOME",
                        SortOrder = 0,
                        IsLeaf = false,
                    },
                    new TypificationNode
                    {
                        NodeId = EntityId.From(TypificationLeafNodeId),
                        ParentNodeId = EntityId.From(TypificationRootNodeId),
                        Label = "Resolved",
                        Code = "RESOLVED",
                        SortOrder = 0,
                        IsLeaf = true,
                        Leaf = new LeafOutcome { Category = TypificationCategory.Success, IsActive = true },
                    },
                ],
                Fields = [],
                DataDips = [],
                AiConfig = new TypificationAiConfig
                {
                    Enabled = false,
                    SentimentGating = false,
                    EntityFieldMap = new Dictionary<string, string>(),
                },
                CreatedAt = DateTimeOffset.UtcNow,
            };

            using var scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITypificationSchemaStore>()
                .SaveAsync(schema, CancellationToken.None).GetAwaiter().GetResult();
            scope.ServiceProvider.GetRequiredService<ISchemaBindingStore>().SaveAsync(
                new SchemaBinding
                {
                    BindingId = EntityId.From($"binding-{Guid.NewGuid():N}"),
                    TenantId = Tenant,
                    Scope = BindingScope.Tenant,
                    ScopeRef = null,
                    SchemaId = schemaId,
                    SubTreeRootNodeId = null,
                    Priority = 10,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None).GetAwaiter().GetResult();

            _schemaSeeded = true;
        }
    }
}
