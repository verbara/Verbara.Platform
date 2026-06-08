using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Runtime typify endpoints (replace the flat <c>/wrapup</c>):
/// <c>GET /conversations/{id}/typification-form</c> resolves the bound published
/// schema for a conversation, and <c>POST /conversations/{id}/typify</c> records a
/// server-validated <c>TypificationSubmission</c>, transitions the conversation to
/// WrapUp, and (when the chosen leaf carries a <c>DialerCode</c> and the conversation
/// is an outbound campaign call) bridges to the Pro Dialer disposition path —
/// the exact behavior the deleted <c>WrapUpConversation</c> handler preserved.
///
/// Seeding mirrors <see cref="TypificationEndpointTests"/> (schema CRUD + publish +
/// tenant binding via the admin endpoints) and <c>SupervisorStuckWorkEndpointTests</c>
/// (Conversations written straight into the wired in-memory store). The Pro campaign
/// stores are the test fakes in <see cref="UnifiedPlatformApiFactory"/>; the campaign
/// store records every call-attempt disposition update so the bridge can be asserted.
/// </summary>
public sealed class TypifyEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private static readonly TenantId s_tenantId = new(AuthenticatedPlatformApiFactory.TestTenantId);

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public TypifyEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // Stable ids so tests can reference the path without re-reading the schema.
    private const string RootNodeId = "root-1";
    private const string LeafNodeId = "leaf-1";
    private const string DialerCodeValue = "SALE";
    private const string RequiredFieldKey = "outcome_notes";

    // A minimal VALID publishable schema: one root non-leaf + one leaf child whose
    // outcome carries a DialerCode (the dialer bridge), plus one REQUIRED text field.
    private static object SchemaBody(string name) => new
    {
        name,
        maxDepth = 5,
        nodes = new object[]
        {
            new
            {
                nodeId = RootNodeId,
                parentNodeId = (string?)null,
                label = "Sales",
                code = "SALES",
                sortOrder = 0,
                isLeaf = false,
                channelApplicability = (string[]?)null,
                leaf = (object?)null,
            },
            new
            {
                nodeId = LeafNodeId,
                parentNodeId = RootNodeId,
                label = "Closed Won",
                code = "CLOSED_WON",
                sortOrder = 0,
                isLeaf = true,
                channelApplicability = (string[]?)null,
                leaf = new
                {
                    category = "Success",
                    triggerRetry = false,
                    retryDelayMinutes = (int?)null,
                    triggerCallback = true,
                    dialerCode = DialerCodeValue,
                    isActive = true,
                },
            },
        },
        fields = new object[]
        {
            new
            {
                fieldId = "field-1",
                key = RequiredFieldKey,
                label = "Outcome Notes",
                type = "Text",
                required = true,
                options = (object[]?)null,
                validation = (object?)null,
                attachToNodeId = (string?)null,
                visibleWhen = (object?)null,
                sortOrder = 0,
            },
        },
    };

    /// <summary>Creates + publishes a schema and binds it tenant-wide; returns the schema id.</summary>
    private async Task<string> SeedPublishedTenantSchemaAsync(string name)
    {
        var createResp = await _client.PostAsync(
            "/api/admin/typification/schemas", JsonContent.Create(SchemaBody(name)));
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var schemaId = JsonNode.Parse(await createResp.Content.ReadAsStringAsync())!["schemaId"]!.GetValue<string>();

        var publishResp = await _client.PostAsync(
            $"/api/admin/typification/schemas/{schemaId}/publish", content: null);
        publishResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var bindResp = await _client.PostAsync("/api/admin/typification/bindings", JsonContent.Create(new
        {
            scope = "Tenant",
            scopeRef = (string?)null,
            schemaId,
            subtreeRootNodeId = (string?)null,
            priority = 10,
        }));
        bindResp.StatusCode.Should().Be(HttpStatusCode.Created);

        return schemaId;
    }

    private async Task<Conversation> SeedConversationAsync(
        ConversationState state = ConversationState.Active,
        ChannelType channel = ChannelType.WebChat,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var conv = new Conversation
        {
            ConversationId = EntityId.New(),
            TenantId = s_tenantId,
            ContactId = EntityId.New(),
            Channel = channel,
            State = state,
            Owner = ConversationOwner.ForAgent(EntityId.From(AuthenticatedPlatformApiFactory.TestUserId)),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (metadata is not null)
            foreach (var (k, v) in metadata)
                conv.SetMetadata(k, v);
        await store.SaveAsync(conv, CancellationToken.None);
        return conv;
    }

    // ─── POST /typify ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Typify_ShouldReject400_WhenRequiredVisibleFieldMissing()
    {
        await SeedPublishedTenantSchemaAsync("Typify Required Field");
        var conv = await SeedConversationAsync();

        // Required field omitted from fieldValues → server validation fails.
        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string>(),
            notes = "missing required",
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["errors"]!.AsArray().Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Typify_ShouldPersistSubmission_WhenValidManual()
    {
        await SeedPublishedTenantSchemaAsync("Typify Persist");
        var conv = await SeedConversationAsync();

        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string> { [RequiredFieldKey] = "done deal" },
            notes = "valid submission",
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["conversationId"]!.GetValue<string>().Should().Be(conv.ConversationId.Value);
        json["leafNodeId"]!.GetValue<string>().Should().Be(LeafNodeId);
        json["source"]!.GetValue<string>().Should().Be("Manual");

        // Persisted via the wired submission store.
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<Verbara.Platform.Typification.Stores.ITypificationSubmissionStore>();
        var saved = await store.GetByConversationIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.LeafNodeId.Value.Should().Be(LeafNodeId);
        saved.FieldValues[RequiredFieldKey].Should().Be("done deal");
    }

    [Fact]
    public async Task Typify_ShouldTransitionToWrapUp_WhenActive()
    {
        await SeedPublishedTenantSchemaAsync("Typify Transition");
        var conv = await SeedConversationAsync(ConversationState.Active);

        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string> { [RequiredFieldKey] = "ok" },
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var updated = await store.GetByIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        updated!.State.Should().Be(ConversationState.WrapUp);
    }

    [Fact]
    public async Task Typify_ShouldBridgeToDialer_WhenLeafHasDialerCodeAndOutboundMetadata()
    {
        await SeedPublishedTenantSchemaAsync("Typify Bridge");

        // Seed a Pro campaign DispositionCode whose Code == the leaf DialerCode.
        const long campaignId = 4242L;
        const long callAttemptId = 99L;
        const long contactId = 7L;
        var dispoStore = _factory.Services.GetRequiredService<InMemoryDispositionCodeStore>();
        await dispoStore.CreateAsync(
            AuthenticatedPlatformApiFactory.TestTenantId,
            new DispositionCode
            {
                TenantId = AuthenticatedPlatformApiFactory.TestTenantId,
                CampaignId = campaignId,
                Code = DialerCodeValue,
                Label = "Sale",
                TriggerCallback = true,
            },
            CancellationToken.None);

        var campaignStore = _factory.Services.GetRequiredService<InMemoryCampaignStore>();
        var before = campaignStore.RecordedDispositionUpdates.Count;

        var conv = await SeedConversationAsync(
            ConversationState.Active,
            ChannelType.Voice,
            metadata: new Dictionary<string, string>
            {
                ["callAttemptId"] = callAttemptId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["campaignId"] = campaignId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["contactId"] = contactId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string>
            {
                [RequiredFieldKey] = "won",
                ["callback_date"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
            },
            notes = "bridge note",
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The call-attempt disposition update ran with the matched DispositionCode id.
        var recorded = campaignStore.RecordedDispositionUpdates.Skip(before).ToList();
        recorded.Should().ContainSingle(r => r.CallAttemptId == callAttemptId);

        // TriggerCallback + a valid callback_date scheduled a callback.
        var callbacks = await campaignStore.GetPendingCallbacksAsync(
            AuthenticatedPlatformApiFactory.TestTenantId, DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
        callbacks.Should().Contain(c => c.CampaignId == campaignId && c.ContactId == contactId);
    }

    [Fact]
    public async Task Typify_ShouldNotBridge_WhenNoCampaignMetadata()
    {
        await SeedPublishedTenantSchemaAsync("Typify No Bridge");

        var campaignStore = _factory.Services.GetRequiredService<InMemoryCampaignStore>();
        var before = campaignStore.RecordedDispositionUpdates.Count;

        // No campaign metadata on the conversation → bridge must not fire.
        var conv = await SeedConversationAsync(ConversationState.Active, ChannelType.WebChat);

        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string> { [RequiredFieldKey] = "no bridge" },
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        campaignStore.RecordedDispositionUpdates.Count.Should().Be(before);
    }

    // ─── GET /typification-form ─────────────────────────────────────────────────

    [Fact]
    public async Task GetTypificationForm_ShouldReturnResolvedSchema_WhenBindingExists()
    {
        var schemaId = await SeedPublishedTenantSchemaAsync("Form Resolve");
        var conv = await SeedConversationAsync();

        var response = await _client.GetAsync($"/api/conversations/{conv.ConversationId.Value}/typification-form");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["schema"]!["schemaId"]!.GetValue<string>().Should().Be(schemaId);
        json["schema"]!["isPublished"]!.GetValue<bool>().Should().BeTrue();
        json["schema"]!["nodes"]!.AsArray().Count.Should().Be(2);
    }

    [Fact]
    public async Task GetTypificationForm_ShouldReturn404_WhenNoBinding()
    {
        // A fresh factory has NO bindings seeded for this conversation → resolver returns null.
        await using var factory = new AuthenticatedPlatformApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        EntityId convId;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
            var conv = new Conversation
            {
                ConversationId = EntityId.New(),
                TenantId = s_tenantId,
                ContactId = EntityId.New(),
                Channel = ChannelType.WebChat,
                State = ConversationState.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await store.SaveAsync(conv, CancellationToken.None);
            convId = conv.ConversationId;
        }

        var response = await client.GetAsync($"/api/conversations/{convId.Value}/typification-form");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
