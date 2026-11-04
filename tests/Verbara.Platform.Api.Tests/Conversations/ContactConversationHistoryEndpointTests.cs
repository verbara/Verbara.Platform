using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// contact-history-opens-by-assignment task 2.3 over the real host (design D5, D6, D8, D9, D14):
/// <c>GET /api/v1/contacts/{id}/conversations</c> answers <c>PagedResult&lt;ContactConversationSummaryDto&gt;</c>
/// through the app's source-generated serializer options, member for member with
/// <c>contact-conversation-history.v1.json</c> (absent members included), 404 for an unknown contact, a clamped
/// <c>pageSize</c>, the disposition label from the submitted schema version, and <c>heldByCaller</c> false on an offer.
/// </summary>
public sealed class ContactConversationHistoryEndpointTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private static readonly TenantId Tenant = ConversationOwnershipApiFactory.Tenant;

    private readonly ConversationOwnershipApiFactory _factory;

    public ContactConversationHistoryEndpointTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    private static string ContactRoute(EntityId contactId) => $"/api/v1/contacts/{contactId.Value}/conversations";

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    private T Store<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    private static JsonObject Fixture()
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", "contact-conversation-history.v1.json");
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    /// <summary>
    /// The fixture's body with its annotations (<c>_example</c>, <c>_omittedWhileNull</c>, <c>_storedLastMessageAt</c>)
    /// removed and every date-time rewritten to one form, so an instant compares by value, not by its spelling
    /// (<c>.000+00:00</c> against <c>+00:00</c>).
    /// </summary>
    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .Where(member => !member.Key.StartsWith('_'))
            .Select(member => KeyValuePair.Create(member.Key, Normalize(member.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Normalize)]),
        JsonValue value when value.TryGetValue<string>(out var text)
            && text.Length >= 20 && text[4] == '-' && text[10] == 'T'
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) =>
            JsonValue.Create(instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        _ => node?.DeepClone(),
    };

    private static async Task<JsonObject> OkBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {body}");
        return JsonNode.Parse(body)!.AsObject();
    }

    private Agent SaveAgent(EntityId agentId, EntityId userId, string displayName)
    {
        var agent = new Agent
        {
            AgentId = agentId,
            TenantId = Tenant,
            UserId = userId,
            DisplayName = displayName,
            State = AgentState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Store<IAgentStore>().SaveAsync(agent, CancellationToken.None).GetAwaiter().GetResult();
        return agent;
    }

    /// <summary>
    /// Saves <paramref name="conversation"/> once per owner of <paramref name="owners"/>, in order, so the store records
    /// the last queue among them (design D7), then once more with <paramref name="conversation"/>'s own owner.
    /// </summary>
    private void SaveConversation(Conversation conversation, params ConversationOwner[] owners)
    {
        var store = Store<IConversationStore>();
        var finalOwner = conversation.Owner;
        foreach (var owner in owners)
        {
            conversation.Owner = owner;
            store.SaveAsync(conversation, CancellationToken.None).GetAwaiter().GetResult();
        }

        conversation.Owner = finalOwner;
        store.SaveAsync(conversation, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void SaveMessage(
        EntityId conversationId, MessageDirection direction, MessageBlock block, DateTimeOffset at,
        MessageDeliveryStatus status = MessageDeliveryStatus.Delivered) =>
        Store<IMessageStore>().SaveAsync(new Message
        {
            MessageId = EntityId.From($"m-{Guid.NewGuid():N}"),
            ConversationId = conversationId,
            TenantId = Tenant,
            Direction = direction,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([block]),
            DeliveryStatus = status,
            CreatedAt = at,
        }, CancellationToken.None).GetAwaiter().GetResult();

    private void SaveSchema(EntityId schemaId, params (string NodeId, string Label)[] leaves)
    {
        var root = EntityId.From($"root-{schemaId.Value}");
        List<TypificationNode> nodes =
        [
            new TypificationNode
            {
                NodeId = root, ParentNodeId = null, Label = "Outcome", Code = "OUTCOME", SortOrder = 0, IsLeaf = false,
            },
        ];
        nodes.AddRange(leaves.Select((leaf, index) => new TypificationNode
        {
            NodeId = EntityId.From(leaf.NodeId),
            ParentNodeId = root,
            Label = leaf.Label,
            Code = $"LEAF{index}",
            SortOrder = index,
            IsLeaf = true,
            Leaf = new LeafOutcome { Category = TypificationCategory.Success, IsActive = true },
        }));
        Store<ITypificationSchemaStore>().SaveAsync(new TypificationSchema
        {
            SchemaId = schemaId,
            TenantId = Tenant,
            Name = $"History schema {schemaId.Value}",
            Version = 1,
            IsPublished = true,
            MaxDepth = 5,
            Nodes = nodes,
            Fields = [],
            DataDips = [],
            AiConfig = new TypificationAiConfig
            {
                Enabled = false, SentimentGating = false, EntityFieldMap = new Dictionary<string, string>(),
            },
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void SaveSubmission(EntityId conversationId, EntityId schemaId, string leaf) =>
        Store<ITypificationSubmissionStore>().TryInsertAsync(new TypificationSubmission
        {
            TenantId = Tenant,
            ConversationId = conversationId,
            AgentId = EntityId.From("agent-typifier"),
            SchemaId = schemaId,
            SchemaVersion = 1,
            SelectedNodePath = [EntityId.From($"root-{schemaId.Value}"), EntityId.From(leaf)],
            LeafNodeId = EntityId.From(leaf),
            FieldValues = new Dictionary<string, string>(),
            CompletedAt = DateTimeOffset.UtcNow,
        }, null, CancellationToken.None).GetAwaiter().GetResult();

    private static Conversation NewConversation(
        string id, EntityId contactId, ConversationState state, DateTimeOffset createdAt, ConversationOwner? owner) =>
        new()
        {
            ConversationId = EntityId.From(id),
            TenantId = Tenant,
            ContactId = contactId,
            Channel = ChannelType.WhatsApp,
            State = state,
            Owner = owner,
            CreatedAt = createdAt,
        };

    [Fact]
    public async Task GetContactConversations_ShouldAnswerTheFixtureMemberForMember_WhenTheHolderReadsTheContact()
    {
        var fixture = Fixture();
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.WhatsApp, $"+57{Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L)}"));
        // The fixture's contact id is in its request path; the route takes whichever id the contact has.
        var contactId = contact.ContactId;
        var queueId = EntityId.From("q-support-0001");
        await Store<IQueueStore>().SaveAsync(
            new Queue { QueueId = queueId, TenantId = Tenant, Name = "Support", CreatedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
        var userB = _factory.NewUser(UserRole.Agent, "fixture-b");
        var agentB = SaveAgent(EntityId.From("a-0f1e2d3c4b5a6978"), userB.UserId, "Agent B");
        var agentA = SaveAgent(EntityId.From("a-9e8d7c6b5a493827"), EntityId.From($"user-{Guid.NewGuid():N}"), "Agent A");

        // Item 1: the conversation B holds now.
        var held = NewConversation("c3b2a1f0e9d84c7b6a5f4e3d2c1b0a99", contactId, ConversationState.Active,
            At("2026-11-03T14:02:11Z"), ConversationOwner.ForAgent(agentB.AgentId));
        SaveConversation(held, ConversationOwner.ForQueue(queueId));
        SaveMessage(held.ConversationId, MessageDirection.Inbound,
            new TextBlock("Hi again, the replacement card has not arrived yet"), At("2026-11-03T14:05:40Z"));

        // Item 2: closed for inactivity three days after its last message, handled and typified by A.
        var closed = NewConversation("4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a", contactId, ConversationState.Closed,
            At("2026-10-28T09:15:00Z"), ConversationOwner.ForAgent(agentA.AgentId));
        closed.ClosedAt = At("2026-10-31T10:20:05Z");
        closed.SetMetadata(ConversationCloseReasons.MetadataKey, ConversationCloseReasons.Inactivity);
        SaveConversation(closed, ConversationOwner.ForQueue(queueId));
        SaveMessage(closed.ConversationId, MessageDirection.Inbound, new TextBlock("When will my card arrive?"),
            At("2026-10-28T09:16:00Z"));
        SaveMessage(closed.ConversationId, MessageDirection.Outbound,
            new TextBlock("Your replacement card has been ordered and should arrive within five business days."),
            At("2026-10-28T10:20:05Z"));
        // A System note after it is never the last message.
        SaveMessage(closed.ConversationId, MessageDirection.System, new TextBlock("Transferred with a note"),
            At("2026-10-28T10:25:00Z"));
        var schemaId = EntityId.From($"schema-{Guid.NewGuid():N}");
        SaveSchema(schemaId, ("n-card-replacement", "Card replacement requested"));
        SaveSubmission(closed.ConversationId, schemaId, "n-card-replacement");

        // Item 3: closed before queues were persisted, never typified, last message an image. Its stored clock is the
        // migration 026 backfill (its created_at). It is saved with no owner: the store records the queue of a queue
        // owner (design D7), and this row is one whose queue was never recorded.
        var old = NewConversation("0a9b8c7d6e5f40312233445566778899", contactId, ConversationState.Abandoned,
            At("2026-09-02T18:00:00Z"), owner: null);
        old.ClosedAt = At("2026-09-02T18:30:00Z");
        old.LastMessageAt = At("2026-09-02T18:00:00Z");
        SaveConversation(old);
        SaveMessage(old.ConversationId, MessageDirection.Inbound,
            new ImageBlock("https://media.example/card.jpg", null, "image/jpeg"), At("2026-09-02T18:01:12Z"));

        using var clientB = _factory.ClientFor(userB);
        var query = fixture["request"]!["query"]!;
        var body = await OkBodyAsync(await clientB.GetAsync(
            $"{ContactRoute(contactId)}?page={query["page"]!.GetValue<string>()}&pageSize={query["pageSize"]!.GetValue<string>()}"));

        var expected = Normalize(fixture["body"]);
        var actual = Normalize(body);
        JsonNode.DeepEquals(actual, expected).Should().BeTrue(
            $"the body must equal the fixture member for member.\nexpected: {expected!.ToJsonString()}\nactual:   {actual!.ToJsonString()}");
    }

    [Fact]
    public async Task GetContactConversations_ShouldAnswer404_WhenTheContactDoesNotExist()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "history-404");
        var (agentUser, _) = _factory.NewAgent("history-404-agent");
        using var supervisorClient = _factory.ClientFor(supervisor);
        using var agentClient = _factory.ClientFor(agentUser);
        var unknown = EntityId.From($"contact-{Guid.NewGuid():N}");

        var bySupervisor = await supervisorClient.GetAsync(ContactRoute(unknown));
        var byAgent = await agentClient.GetAsync(ContactRoute(unknown));

        bySupervisor.StatusCode.Should().Be(HttpStatusCode.NotFound, "an unknown contact answers 404, not an empty page (design D5 step 1)");
        byAgent.StatusCode.Should().Be(HttpStatusCode.NotFound, "the 404 comes before the read rule");
        (await bySupervisor.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("pageSize=0", 1, 1)]
    [InlineData("pageSize=-5", 1, 1)]
    [InlineData("pageSize=500", 1, 100)]
    [InlineData("page=0&pageSize=2", 1, 2)]
    [InlineData("", 1, 25)]
    public async Task GetContactConversations_ShouldClampThePage_WhenThePagingIsOutOfRange(string paging, int page, int pageSize)
    {
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}"));
        _factory.SeedConversation(ConversationOwner.ForAgent(EntityId.From("agent-x")), ConversationState.Closed, contactId: contact.ContactId);
        _factory.SeedConversation(ConversationOwner.ForAgent(EntityId.From("agent-y")), ConversationState.Closed, contactId: contact.ContactId);
        using var client = _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, "history-clamp"));

        var body = await OkBodyAsync(await client.GetAsync($"{ContactRoute(contact.ContactId)}?{paging}"));

        body["page"]!.GetValue<int>().Should().Be(page);
        body["pageSize"]!.GetValue<int>().Should().Be(pageSize, "pageSize is clamped to 1..100 (design D5)");
        body["items"]!.AsArray().Should().HaveCount(Math.Min(pageSize, 2));
        body["totalCount"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task GetContactConversations_ShouldFlagTheOfferAsNotHeld_WhenTheHolderIsAlsoOfferedAnotherConversationOfTheContact()
    {
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}"));
        var (userA, agentA) = _factory.NewAgent("history-holder");
        var held = _factory.SeedConversation(ConversationOwner.ForAgent(agentA.AgentId), ConversationState.Active, contactId: contact.ContactId);
        var offered = _factory.SeedOfferTo(agentA, _factory.SeedQueue(), contactId: contact.ContactId);
        using var clientA = _factory.ClientFor(userA);

        var items = (await OkBodyAsync(await clientA.GetAsync(ContactRoute(contact.ContactId))))["items"]!.AsArray();

        items.ToDictionary(item => item!["id"]!.GetValue<string>(), item => item!["heldByCaller"]!.GetValue<bool>())
            .Should().BeEquivalentTo(new Dictionary<string, bool>
            {
                [held.ConversationId.Value] = true,
                [offered.ConversationId.Value] = false,
            }, "an open offer is a preview, not a hold (design D3, Q3)");
    }

    [Fact]
    public async Task GetContactConversations_ShouldResolveTheLabelOfTheCorrectedLeaf_WhenASupervisorCorrectedTheDisposition()
    {
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}"));
        var schemaId = EntityId.From($"schema-{Guid.NewGuid():N}");
        SaveSchema(schemaId, ("n-a", "Label A"), ("n-b", "Label B"));
        var corrected = _factory.SeedConversation(ConversationOwner.ForAgent(EntityId.From("agent-x")), ConversationState.Closed, contactId: contact.ContactId);
        SaveSubmission(corrected.ConversationId, schemaId, "n-a");
        await Store<ITypificationSubmissionCorrectionStore>().InsertAsync(new TypificationSubmissionCorrection
        {
            TenantId = Tenant,
            ConversationId = corrected.ConversationId,
            CorrectedLeafNodeId = EntityId.From("n-b"),
            CorrectedNodePath = [EntityId.From($"root-{schemaId.Value}"), EntityId.From("n-b")],
            CorrectedByUserId = "supervisor-1",
            CorrectedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        var gone = _factory.SeedConversation(ConversationOwner.ForAgent(EntityId.From("agent-x")), ConversationState.Closed, contactId: contact.ContactId);
        SaveSubmission(gone.ConversationId, schemaId, "n-not-in-the-schema");
        using var client = _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, "history-disposition"));

        var items = (await OkBodyAsync(await client.GetAsync(ContactRoute(contact.ContactId))))["items"]!.AsArray();

        JsonNode.DeepEquals(
            items.Single(item => item!["id"]!.GetValue<string>() == corrected.ConversationId.Value)!["disposition"],
            new JsonObject { ["nodeId"] = "n-b", ["label"] = "Label B" }).Should().BeTrue("the correction wins (ADR-0034)");
        JsonNode.DeepEquals(
            items.Single(item => item!["id"]!.GetValue<string>() == gone.ConversationId.Value)!["disposition"],
            new JsonObject { ["nodeId"] = "n-not-in-the-schema" }).Should().BeTrue("a node that no longer resolves keeps nodeId and omits label");
    }
}
