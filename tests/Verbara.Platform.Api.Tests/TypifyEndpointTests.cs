using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.Dialer.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Runtime typify endpoints (replace the flat <c>/wrapup</c>):
/// <c>GET /conversations/{id}/typification-form</c> resolves the bound published
/// schema for a conversation (+ C9 wrap-up PREFILL: a preselected reason node path
/// and prefilled field values derived from the conversation's captured context), and
/// <c>POST /conversations/{id}/typify</c> records a server-validated
/// <c>TypificationSubmission</c>, transitions the conversation to WrapUp, and (when the
/// chosen leaf carries a <c>DialerCode</c> and the conversation is an outbound campaign
/// call) bridges to the Pro Dialer disposition path — the exact behavior the deleted
/// <c>WrapUpConversation</c> handler preserved.
///
/// <para>
/// <b>Isolation (flake fix):</b> the typification schema/binding/conversation stores are
/// process-wide singletons registered by <c>AddInMemoryStorage()</c>. The previous
/// <c>IClassFixture</c> shared a SINGLE factory across the whole class, so every
/// <see cref="SeedPublishedTenantSchemaAsync"/> appended ANOTHER tenant-wide binding for
/// the same tenant — all at priority 10. The resolver tie-breaks equal-priority bindings
/// by <c>BindingId.Value</c> ordinal (a server-minted random EntityId), so
/// <c>GetTypificationForm_ShouldReturnResolvedSchema_WhenBindingExists</c> intermittently
/// resolved a DIFFERENT test's schema and failed its id assertion. Each test now owns a
/// fresh factory (clean store graph), so no cross-test binding accumulation can occur.
/// </para>
/// </summary>
public sealed class TypifyEndpointTests : IDisposable
{
    private static readonly TenantId s_tenantId = new(AuthenticatedPlatformApiFactory.TestTenantId);

    private readonly AuthenticatedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public TypifyEndpointTests()
    {
        _factory = new AuthenticatedPlatformApiFactory();
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // Stable ids so tests can reference the path without re-reading the schema.
    private const string RootNodeId = "root-1";
    private const string LeafNodeId = "leaf-1";
    private const string RootCode = "SALES";
    private const string LeafCode = "CLOSED_WON";
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
                code = RootCode,
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
                code = LeafCode,
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

    /// <summary>
    /// Seeds a published schema + tenant-wide binding DIRECTLY into the in-memory stores.
    /// Needed for prefill assertions because the admin schema create DTO does NOT carry a
    /// field's <see cref="TypificationField.PrefillSource"/>, so it cannot be expressed over
    /// HTTP — direct seeding is the only way to exercise the metadata field-prefill path.
    /// </summary>
    private async Task<EntityId> SeedSchemaWithStoreAsync(
        IReadOnlyList<TypificationField>? fields = null)
    {
        var schemaId = EntityId.New();
        var schema = new TypificationSchema
        {
            SchemaId = schemaId,
            TenantId = s_tenantId,
            Name = "Direct Seeded Schema",
            Version = 1,
            IsPublished = true,
            MaxDepth = 5,
            Nodes =
            [
                new TypificationNode
                {
                    NodeId = EntityId.From(RootNodeId),
                    ParentNodeId = null,
                    Label = "Sales",
                    Code = RootCode,
                    SortOrder = 0,
                    IsLeaf = false,
                },
                new TypificationNode
                {
                    NodeId = EntityId.From(LeafNodeId),
                    ParentNodeId = EntityId.From(RootNodeId),
                    Label = "Closed Won",
                    Code = LeafCode,
                    SortOrder = 0,
                    IsLeaf = true,
                    Leaf = new LeafOutcome { Category = TypificationCategory.Success, IsActive = true },
                },
            ],
            Fields = fields ?? [],
            DataDips = [],
            AiConfig = new TypificationAiConfig
            {
                Enabled = false,
                SentimentGating = false,
                EntityFieldMap = new Dictionary<string, string>(),
            },
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using var scope = _factory.Services.CreateScope();
        var schemaStore = scope.ServiceProvider.GetRequiredService<ITypificationSchemaStore>();
        var bindingStore = scope.ServiceProvider.GetRequiredService<ISchemaBindingStore>();

        await schemaStore.SaveAsync(schema, CancellationToken.None);
        await bindingStore.SaveAsync(
            new SchemaBinding
            {
                BindingId = EntityId.New(),
                TenantId = s_tenantId,
                Scope = BindingScope.Tenant,
                ScopeRef = null,
                SchemaId = schemaId,
                SubTreeRootNodeId = null,
                Priority = 10,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);

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
    public async Task Typify_ShouldNotTransitionToWrapUp_WhenValidationFails()
    {
        await SeedPublishedTenantSchemaAsync("Typify No Mutation On Invalid");
        var conv = await SeedConversationAsync(ConversationState.Active);

        // Required field omitted → server validation fails BEFORE any state mutation.
        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string>(),
            notes = "should not mutate",
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = _factory.Services.CreateScope();

        // Conversation must STILL be Active (NOT transitioned to WrapUp).
        var conversationStore = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var unchanged = await conversationStore.GetByIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        unchanged!.State.Should().Be(ConversationState.Active);

        // No TypificationSubmission was persisted.
        var submissionStore = scope.ServiceProvider
            .GetRequiredService<Verbara.Platform.Typification.Stores.ITypificationSubmissionStore>();
        var saved = await submissionStore.GetByConversationIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        saved.Should().BeNull();
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

    // ─── GET /typification-form — C9 PREFILL ────────────────────────────────────

    [Fact]
    public async Task GetTypificationForm_ShouldReturnPrefilledNodePath_WhenReasonPathMetadataPresent()
    {
        var schemaId = await SeedPublishedTenantSchemaAsync("Form Prefill Path");

        // reasonPath = a valid Codes JSON for the bound schema (root→leaf).
        var reasonPath = JsonSerializer.Serialize(new[] { RootCode, LeafCode });
        var conv = await SeedConversationAsync(
            metadata: new Dictionary<string, string>
            {
                [TypificationMetadataKeys.ReasonPath] = reasonPath,
            });

        var response = await _client.GetAsync($"/api/conversations/{conv.ConversationId.Value}/typification-form");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["schema"]!["schemaId"]!.GetValue<string>().Should().Be(schemaId);

        // Prefilled node path resolves the captured Codes → the matching node-id path.
        var path = json["prefilledNodePath"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        path.Should().Equal(RootNodeId, LeafNodeId);
    }

    [Fact]
    public async Task GetTypificationForm_ShouldReturnPrefilledFieldValues_WhenMetadataPrefillFieldsResolve()
    {
        // A schema field with PrefillSource{Kind:Metadata, Ref:"patientId"}. The admin DTO
        // now round-trips PrefillSource (C9b); this test seeds the store directly to keep the
        // prefill-resolution assertion focused and independent of the admin write path.
        const string fieldKey = "patient_id";
        var field = new TypificationField
        {
            FieldId = EntityId.New(),
            Key = fieldKey,
            Label = "Patient Id",
            Type = FieldType.Text,
            Required = false,
            PrefillSource = new PrefillRef { Kind = PrefillSourceKind.Metadata, Ref = "patientId" },
            SortOrder = 0,
        };
        await SeedSchemaWithStoreAsync(fields: [field]);

        var conv = await SeedConversationAsync(
            metadata: new Dictionary<string, string> { ["patientId"] = "X" });

        var response = await _client.GetAsync($"/api/conversations/{conv.ConversationId.Value}/typification-form");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["prefilledFieldValues"]![fieldKey]!.GetValue<string>().Should().Be("X");
    }

    [Fact]
    public async Task GetTypificationForm_ShouldReturnNullPrefill_WhenNoMetadata()
    {
        await SeedPublishedTenantSchemaAsync("Form No Prefill");

        // No metadata → no reasonPath + no metadata-sourced field values.
        var conv = await SeedConversationAsync();

        var response = await _client.GetAsync($"/api/conversations/{conv.ConversationId.Value}/typification-form");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        // Schema is still returned; both prefill members are null (omitted when null).
        json!["schema"]!["schemaId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        json["prefilledNodePath"].Should().BeNull();
        json["prefilledFieldValues"].Should().BeNull();
    }

    // ─── POST /typify — B3 server-authoritative AI provenance ──────────────────

    /// <summary>
    /// Seeds an AI suggestion for the conversation (leaf X), then submits typify
    /// with the SAME leaf → provenance service derives AiAccepted=true, Source=AutoAi.
    /// </summary>
    [Fact]
    public async Task Typify_ShouldDeriveAiAcceptedTrue_WhenCommittedLeafMatchesSuggestion()
    {
        await SeedPublishedTenantSchemaAsync("B3 Accepted");
        var conv = await SeedConversationAsync();

        // Seed a stored suggestion: the AI suggested the same leaf the agent will pick.
        var suggestedLeaf = EntityId.From(LeafNodeId);
        using (var scope = _factory.Services.CreateScope())
        {
            var suggestionStore = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            await suggestionStore.SaveAsync(new AiSuggestionRecord
            {
                Id = EntityId.New(),
                TenantId = EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId),
                ConversationId = conv.ConversationId,
                SchemaId = EntityId.New(),
                SchemaVersion = 1,
                SuggestedLeafNodeId = suggestedLeaf,
                SuggestedNodePath = [RootNodeId, LeafNodeId],
                SuggestedFieldValues = new Dictionary<string, string>(),
                Confidence = 0.92,
                ModelId = "gpt-4o-mini",
                PromptVersion = "v1",
                SurfacedBand = TypificationBand.Suggest,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string> { [RequiredFieldKey] = "ai matched" },
            // Client sends false — server MUST ignore and derive true from suggestion match.
            aiSuggested = false,
            aiAccepted = false,
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["aiSuggested"]!.GetValue<bool>().Should().BeTrue();
        json["aiAccepted"]!.GetValue<bool>().Should().BeTrue();
        json["source"]!.GetValue<string>().Should().Be("AutoAi");
        json["suggestedLeafNodeId"]!.GetValue<string>().Should().Be(LeafNodeId);

        using var scope2 = _factory.Services.CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var saved = await store.GetByConversationIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.AiSuggested.Should().BeTrue();
        saved.AiAccepted.Should().BeTrue();
        saved.Source.Should().Be(SubmissionSource.AutoAi);
        saved.SuggestedLeafNodeId.Should().Be(EntityId.From(LeafNodeId));
    }

    /// <summary>
    /// Stored suggestion leaf X; agent commits leaf Y → AiAccepted=false, Source=Manual,
    /// SuggestedLeafNodeId=X, LeafNodeId=Y.
    /// </summary>
    [Fact]
    public async Task Typify_ShouldDeriveAiAcceptedFalse_WhenAgentOverrides()
    {
        // Use a schema where we have a second leaf to commit different from the suggestion.
        // We'll seed the schema directly so we can control node IDs.
        const string overrideLeafId = "leaf-override";
        const string overrideLeafCode = "OVERRIDE_LEAF";

        var schemaId = EntityId.New();
        using (var scope = _factory.Services.CreateScope())
        {
            var schemaStore = scope.ServiceProvider.GetRequiredService<ITypificationSchemaStore>();
            var bindingStore = scope.ServiceProvider.GetRequiredService<ISchemaBindingStore>();

            await schemaStore.SaveAsync(new TypificationSchema
            {
                SchemaId = schemaId,
                TenantId = s_tenantId,
                Name = "B3 Override",
                Version = 1,
                IsPublished = true,
                MaxDepth = 5,
                Nodes =
                [
                    new TypificationNode
                    {
                        NodeId = EntityId.From(RootNodeId),
                        ParentNodeId = null,
                        Label = "Root",
                        Code = RootCode,
                        SortOrder = 0,
                        IsLeaf = false,
                    },
                    new TypificationNode
                    {
                        NodeId = EntityId.From(LeafNodeId),
                        ParentNodeId = EntityId.From(RootNodeId),
                        Label = "AI Suggested Leaf",
                        Code = LeafCode,
                        SortOrder = 0,
                        IsLeaf = true,
                        Leaf = new LeafOutcome { Category = TypificationCategory.Success, IsActive = true },
                    },
                    new TypificationNode
                    {
                        NodeId = EntityId.From(overrideLeafId),
                        ParentNodeId = EntityId.From(RootNodeId),
                        Label = "Agent Override Leaf",
                        Code = overrideLeafCode,
                        SortOrder = 1,
                        IsLeaf = true,
                        Leaf = new LeafOutcome { Category = TypificationCategory.Success, IsActive = true },
                    },
                ],
                Fields = [],
                DataDips = [],
                AiConfig = new TypificationAiConfig { Enabled = false, SentimentGating = false, EntityFieldMap = new Dictionary<string, string>() },
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);

            await bindingStore.SaveAsync(new SchemaBinding
            {
                BindingId = EntityId.New(),
                TenantId = s_tenantId,
                Scope = BindingScope.Tenant,
                ScopeRef = null,
                SchemaId = schemaId,
                SubTreeRootNodeId = null,
                Priority = 5,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        var conv = await SeedConversationAsync();

        // Seed suggestion: AI suggested LeafNodeId ("leaf-1").
        using (var scope = _factory.Services.CreateScope())
        {
            var suggestionStore = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            await suggestionStore.SaveAsync(new AiSuggestionRecord
            {
                Id = EntityId.New(),
                TenantId = EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId),
                ConversationId = conv.ConversationId,
                SchemaId = schemaId,
                SchemaVersion = 1,
                SuggestedLeafNodeId = EntityId.From(LeafNodeId),
                SuggestedNodePath = [RootNodeId, LeafNodeId],
                SuggestedFieldValues = new Dictionary<string, string>(),
                Confidence = 0.75,
                ModelId = "gpt-4o-mini",
                PromptVersion = "v1",
                SurfacedBand = TypificationBand.Suggest,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        // Agent commits "leaf-override" — different from the AI suggestion.
        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, overrideLeafId },
            fieldValues = new Dictionary<string, string>(),
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["aiSuggested"]!.GetValue<bool>().Should().BeTrue();
        json["aiAccepted"]!.GetValue<bool>().Should().BeFalse();
        json["source"]!.GetValue<string>().Should().Be("Manual");
        json["leafNodeId"]!.GetValue<string>().Should().Be(overrideLeafId);
        json["suggestedLeafNodeId"]!.GetValue<string>().Should().Be(LeafNodeId);

        using var scope2 = _factory.Services.CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var saved = await store.GetByConversationIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.AiAccepted.Should().BeFalse();
        saved.Source.Should().Be(SubmissionSource.Manual);
        saved.SuggestedLeafNodeId.Should().Be(EntityId.From(LeafNodeId));
        saved.LeafNodeId.Value.Should().Be(overrideLeafId);
    }

    /// <summary>
    /// Client body claims AiSuggested=true but no stored suggestion exists →
    /// server must derive AiSuggested=false, Source=Manual.
    /// </summary>
    [Fact]
    public async Task Typify_ShouldIgnoreClientAssertedAiFlags_WhenNoStoredSuggestion()
    {
        await SeedPublishedTenantSchemaAsync("B3 No Suggestion");
        var conv = await SeedConversationAsync();

        // No suggestion seeded — client lying about AI involvement.
        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string> { [RequiredFieldKey] = "client lying" },
            aiSuggested = true,
            aiAccepted = true,
            aiConfidence = 0.99,
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        // Server ignored client-supplied values.
        json!["aiSuggested"]!.GetValue<bool>().Should().BeFalse();
        json["source"]!.GetValue<string>().Should().Be("Manual");
        // aiAccepted and aiConfidence should be null (omitted in JSON when null).
        json["aiAccepted"].Should().BeNull();
        json["aiConfidence"].Should().BeNull();

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var saved = await store.GetByConversationIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.AiSuggested.Should().BeFalse();
        saved.AiAccepted.Should().BeNull();
        saved.Source.Should().Be(SubmissionSource.Manual);
        saved.SuggestedLeafNodeId.Should().BeNull();
        saved.SuggestedNodePath.Should().BeNull();
    }

    // ─── POST /typify — D3 PII screen on the AI write path ─────────────────────

    /// <summary>
    /// A stored suggestion matches the committed leaf → Source=AutoAi, so the persisted
    /// free-text field value is PII-screened (default DenyAll policy): the raw email is
    /// replaced with the [EMAIL] token before storage (defense-in-depth re-screen at commit).
    /// </summary>
    [Fact]
    public async Task Typify_ShouldScreenPiiInFreeText_WhenDispositionIsAutoAi()
    {
        await SeedPublishedTenantSchemaAsync("D3 Screen AutoAi");
        var conv = await SeedConversationAsync();

        // Seed a suggestion for the same leaf the agent will commit → provenance derives AutoAi.
        using (var scope = _factory.Services.CreateScope())
        {
            var suggestionStore = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            await suggestionStore.SaveAsync(new AiSuggestionRecord
            {
                Id = EntityId.New(),
                TenantId = EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId),
                ConversationId = conv.ConversationId,
                SchemaId = EntityId.New(),
                SchemaVersion = 1,
                SuggestedLeafNodeId = EntityId.From(LeafNodeId),
                SuggestedNodePath = [RootNodeId, LeafNodeId],
                SuggestedFieldValues = new Dictionary<string, string>(),
                Confidence = 0.9,
                ModelId = "gpt-4o-mini",
                PromptVersion = "v1",
                SurfacedBand = TypificationBand.Suggest,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string>
            {
                [RequiredFieldKey] = "contact me at john@example.com please",
            },
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope2 = _factory.Services.CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var saved = await store.GetByConversationIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.Source.Should().Be(SubmissionSource.AutoAi);

        // Email masked: token present, raw email absent.
        saved.FieldValues[RequiredFieldKey].Should().Contain("[EMAIL]");
        saved.FieldValues[RequiredFieldKey].Should().NotContain("john@example.com");
    }

    /// <summary>
    /// No stored suggestion → Source=Manual, so human-captured free-text is persisted
    /// UNMASKED — the PII screen only runs on the AI write path (current behavior preserved).
    /// </summary>
    [Fact]
    public async Task Typify_ShouldNotScreenFreeText_WhenDispositionIsManual()
    {
        await SeedPublishedTenantSchemaAsync("D3 No Screen Manual");
        var conv = await SeedConversationAsync();

        // No suggestion seeded → Source=Manual.
        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string>
            {
                [RequiredFieldKey] = "contact me at john@example.com please",
            },
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var saved = await store.GetByConversationIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.Source.Should().Be(SubmissionSource.Manual);

        // Human value persists verbatim — NOT screened.
        saved.FieldValues[RequiredFieldKey].Should().Be("contact me at john@example.com please");
    }

    [Fact]
    public async Task GetTypificationForm_ShouldNotBeLicenseGated_WhenTenantUnlicensed()
    {
        // The conversation-runtime group carries NO LicenseFeatureMetadata, so the
        // license gate middleware must short-circuit regardless of license state — an
        // unlicensed tenant can still close conversations. Asserting NEVER 402.
        //
        // WithWebHostBuilder.ConfigureServices runs AFTER the base factory's own
        // registrations, and AddNoProFeaturesLicensed() removes any prior ILicenseStatus
        // — so the no-features substitute wins over the base all-features one. A bound
        // schema is seeded so a license-gated endpoint WOULD reach the gate.
        using var unlicensedFactory = _factory.WithWebHostBuilder(static builder =>
            builder.ConfigureServices(static services => services.AddNoProFeaturesLicensed()));
        using var client = unlicensedFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", AuthenticatedPlatformApiFactory.TestTenantId);

        // WithWebHostBuilder spins up a SEPARATE DI container, so seed directly into THIS
        // factory's stores (not the base _factory's).
        var schemaId = EntityId.New();
        var convId = EntityId.New();
        using (var scope = unlicensedFactory.Services.CreateScope())
        {
            var schemaStore = scope.ServiceProvider.GetRequiredService<ITypificationSchemaStore>();
            var bindingStore = scope.ServiceProvider.GetRequiredService<ISchemaBindingStore>();
            var conversationStore = scope.ServiceProvider.GetRequiredService<IConversationStore>();

            await schemaStore.SaveAsync(
                new TypificationSchema
                {
                    SchemaId = schemaId,
                    TenantId = s_tenantId,
                    Name = "Unlicensed Runtime Schema",
                    Version = 1,
                    IsPublished = true,
                    MaxDepth = 5,
                    Nodes =
                    [
                        new TypificationNode
                        {
                            NodeId = EntityId.From(RootNodeId),
                            ParentNodeId = null,
                            Label = "Sales",
                            Code = RootCode,
                            SortOrder = 0,
                            IsLeaf = false,
                        },
                        new TypificationNode
                        {
                            NodeId = EntityId.From(LeafNodeId),
                            ParentNodeId = EntityId.From(RootNodeId),
                            Label = "Closed Won",
                            Code = LeafCode,
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
                },
                CancellationToken.None);

            await bindingStore.SaveAsync(
                new SchemaBinding
                {
                    BindingId = EntityId.New(),
                    TenantId = s_tenantId,
                    Scope = BindingScope.Tenant,
                    ScopeRef = null,
                    SchemaId = schemaId,
                    SubTreeRootNodeId = null,
                    Priority = 10,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None);

            await conversationStore.SaveAsync(
                new Conversation
                {
                    ConversationId = convId,
                    TenantId = s_tenantId,
                    ContactId = EntityId.New(),
                    Channel = ChannelType.WebChat,
                    State = ConversationState.Active,
                    Owner = ConversationOwner.ForAgent(EntityId.From(AuthenticatedPlatformApiFactory.TestUserId)),
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None);
        }

        var response = await client.GetAsync($"/api/conversations/{convId.Value}/typification-form");

        // Bound schema → 200 (NEVER 402). The runtime endpoint is not license-gated.
        response.StatusCode.Should().NotBe(HttpStatusCode.PaymentRequired);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        json!["schema"]!["schemaId"]!.GetValue<string>().Should().Be(schemaId.Value);
    }

    // ─── POST /typify — B4b AI-disposition audit entries ─────────────────────

    /// <summary>
    /// Stored suggestion leaf X; agent submits the same leaf X →
    /// an "typification.ai_disposition" audit entry is written with accepted=true
    /// and full AI provenance (modelId, promptVersion, schemaVersion, confidence).
    /// </summary>
    [Fact]
    public async Task Typify_ShouldWriteAiDispositionAudit_WhenSuggestionAccepted()
    {
        await SeedPublishedTenantSchemaAsync("B4b Audit Accepted");
        var conv = await SeedConversationAsync();

        // Seed a suggestion with known provenance fields.
        using (var scope = _factory.Services.CreateScope())
        {
            var suggestionStore = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            await suggestionStore.SaveAsync(new AiSuggestionRecord
            {
                Id = EntityId.New(),
                TenantId = EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId),
                ConversationId = conv.ConversationId,
                SchemaId = EntityId.New(),
                SchemaVersion = 3,
                SuggestedLeafNodeId = EntityId.From(LeafNodeId),
                SuggestedNodePath = [RootNodeId, LeafNodeId],
                SuggestedFieldValues = new Dictionary<string, string>(),
                Confidence = 0.88,
                ModelId = "gpt-4o-mini",
                PromptVersion = "v2",
                SurfacedBand = TypificationBand.Suggest,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        // Agent commits the same leaf (accepts the AI suggestion).
        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string> { [RequiredFieldKey] = "audit accepted" },
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify audit entry was written with AI provenance.
        using var auditScope = _factory.Services.CreateScope();
        var auditStore = auditScope.ServiceProvider.GetRequiredService<Verbara.Platform.Audit.IAuditStore>();
        var hits = await auditStore.SearchAsync(
            s_tenantId,
            new Verbara.Platform.Audit.AuditQuery(Action: "typification.ai_disposition", Page: 1, PageSize: 50),
            CancellationToken.None);

        hits.Items.Should().ContainSingle(e => e.TargetId == conv.ConversationId.Value);
        var entry = hits.Items.First(e => e.TargetId == conv.ConversationId.Value);
        entry.Category.Should().Be("conversations");
        entry.ActorType.Should().Be("user");
        entry.TargetType.Should().Be("Conversation");
        entry.Metadata.Should().NotBeNull();
        entry.Metadata!["modelId"].Should().Be("gpt-4o-mini");
        entry.Metadata["promptVersion"].Should().Be("v2");
        entry.Metadata["schemaVersion"].Should().Be("3");
        entry.Metadata["accepted"].Should().Be("true");
        entry.Metadata["committedLeafNodeId"].Should().Be(LeafNodeId);
        entry.Metadata["suggestedLeafNodeId"].Should().Be(LeafNodeId);
        entry.IntegrityHash.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Stored suggestion leaf X; agent commits leaf Y (override) →
    /// audit entry with accepted=false, suggestedLeafNodeId=X, committedLeafNodeId=Y.
    /// </summary>
    [Fact]
    public async Task Typify_ShouldWriteAiDispositionAudit_WhenAgentOverrides()
    {
        const string overrideLeafId = "leaf-audit-override";

        // Seed a schema with two leaves directly so we can control node IDs.
        var schemaId = EntityId.New();
        using (var scope = _factory.Services.CreateScope())
        {
            var schemaStore = scope.ServiceProvider.GetRequiredService<ITypificationSchemaStore>();
            var bindingStore = scope.ServiceProvider.GetRequiredService<ISchemaBindingStore>();

            await schemaStore.SaveAsync(new TypificationSchema
            {
                SchemaId = schemaId,
                TenantId = s_tenantId,
                Name = "B4b Audit Override",
                Version = 1,
                IsPublished = true,
                MaxDepth = 5,
                Nodes =
                [
                    new TypificationNode
                    {
                        NodeId = EntityId.From(RootNodeId),
                        ParentNodeId = null,
                        Label = "Root",
                        Code = RootCode,
                        SortOrder = 0,
                        IsLeaf = false,
                    },
                    new TypificationNode
                    {
                        NodeId = EntityId.From(LeafNodeId),
                        ParentNodeId = EntityId.From(RootNodeId),
                        Label = "AI Suggested",
                        Code = LeafCode,
                        SortOrder = 0,
                        IsLeaf = true,
                        Leaf = new LeafOutcome { Category = TypificationCategory.Success, IsActive = true },
                    },
                    new TypificationNode
                    {
                        NodeId = EntityId.From(overrideLeafId),
                        ParentNodeId = EntityId.From(RootNodeId),
                        Label = "Agent Override",
                        Code = "OVERRIDE",
                        SortOrder = 1,
                        IsLeaf = true,
                        Leaf = new LeafOutcome { Category = TypificationCategory.Success, IsActive = true },
                    },
                ],
                Fields = [],
                DataDips = [],
                AiConfig = new TypificationAiConfig { Enabled = false, SentimentGating = false, EntityFieldMap = new Dictionary<string, string>() },
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);

            await bindingStore.SaveAsync(new SchemaBinding
            {
                BindingId = EntityId.New(),
                TenantId = s_tenantId,
                Scope = BindingScope.Tenant,
                ScopeRef = null,
                SchemaId = schemaId,
                SubTreeRootNodeId = null,
                Priority = 3,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        var conv = await SeedConversationAsync();

        // AI suggested LeafNodeId; agent will commit overrideLeafId.
        using (var scope = _factory.Services.CreateScope())
        {
            var suggestionStore = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            await suggestionStore.SaveAsync(new AiSuggestionRecord
            {
                Id = EntityId.New(),
                TenantId = EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId),
                ConversationId = conv.ConversationId,
                SchemaId = schemaId,
                SchemaVersion = 1,
                SuggestedLeafNodeId = EntityId.From(LeafNodeId),
                SuggestedNodePath = [RootNodeId, LeafNodeId],
                SuggestedFieldValues = new Dictionary<string, string>(),
                Confidence = 0.70,
                ModelId = "gpt-4o-mini",
                PromptVersion = "v1",
                SurfacedBand = TypificationBand.Suggest,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        // Agent commits the override leaf.
        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, overrideLeafId },
            fieldValues = new Dictionary<string, string>(),
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var auditScope = _factory.Services.CreateScope();
        var auditStore = auditScope.ServiceProvider.GetRequiredService<Verbara.Platform.Audit.IAuditStore>();
        var hits = await auditStore.SearchAsync(
            s_tenantId,
            new Verbara.Platform.Audit.AuditQuery(Action: "typification.ai_disposition", Page: 1, PageSize: 50),
            CancellationToken.None);

        hits.Items.Should().ContainSingle(e => e.TargetId == conv.ConversationId.Value);
        var entry = hits.Items.First(e => e.TargetId == conv.ConversationId.Value);
        entry.Metadata.Should().NotBeNull();
        entry.Metadata!["accepted"].Should().Be("false");
        entry.Metadata["suggestedLeafNodeId"].Should().Be(LeafNodeId);
        entry.Metadata["committedLeafNodeId"].Should().Be(overrideLeafId);
        entry.IntegrityHash.Should().NotBeNullOrEmpty();
    }

    // ─── POST /typify — C1 regression: no premature reconcile on validation failure ──

    /// <summary>
    /// C1 regression (D3 split fix): the stored suggestion's leaf MATCHES the leaf the
    /// request commits — so the (mutating) <see cref="ITypificationProvenanceService.DeriveAsync"/>
    /// WOULD reconcile + bump metrics — BUT the submission is INVALID (required field omitted),
    /// so validation fails (400). The reconcile must NOT have fired: the stored suggestion must
    /// remain un-reconciled (<c>Accepted == null</c>, <c>CommittedLeafNodeId == null</c>) and NO
    /// <see cref="TypificationSubmission"/> may be persisted. Pre-fix this reconciled the
    /// suggestion before validation, polluting calibration accuracy.
    /// </summary>
    [Fact]
    public async Task Typify_ShouldNotReconcileSuggestion_WhenValidationFails()
    {
        await SeedPublishedTenantSchemaAsync("C1 No Premature Reconcile");
        var conv = await SeedConversationAsync();

        // Seed a suggestion whose leaf == the leaf the request commits → DeriveAsync WOULD reconcile.
        var suggestionId = EntityId.New();
        using (var scope = _factory.Services.CreateScope())
        {
            var suggestionStore = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            await suggestionStore.SaveAsync(new AiSuggestionRecord
            {
                Id = suggestionId,
                TenantId = EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId),
                ConversationId = conv.ConversationId,
                SchemaId = EntityId.New(),
                SchemaVersion = 1,
                SuggestedLeafNodeId = EntityId.From(LeafNodeId),
                SuggestedNodePath = [RootNodeId, LeafNodeId],
                SuggestedFieldValues = new Dictionary<string, string>(),
                Confidence = 0.95,
                ModelId = "gpt-4o-mini",
                PromptVersion = "v1",
                SurfacedBand = TypificationBand.Suggest,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        // INVALID submission: required field omitted → server validation fails.
        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string>(),
            notes = "invalid: missing required field",
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope2 = _factory.Services.CreateScope();

        // The suggestion must remain UN-reconciled — DeriveAsync must not have run.
        var suggestions = scope2.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
        var stored = await suggestions.GetLatestForConversationAsync(
            EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId), conv.ConversationId, CancellationToken.None);
        stored.Should().NotBeNull();
        stored!.Accepted.Should().BeNull();
        stored.CommittedLeafNodeId.Should().BeNull();

        // No submission persisted (validation rejected before any commit).
        var submissionStore = scope2.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var saved = await submissionStore.GetByConversationIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        saved.Should().BeNull();
    }

    /// <summary>
    /// A stored suggestion matches the committed leaf → Source=AutoAi, so the PII screen runs —
    /// but it only screens FREE-TEXT field values (Text/Textarea/Lookup). A <c>Phone</c>-type
    /// field legitimately holds a phone number, so its value must persist RAW (not masked to
    /// <c>[PHONE]</c>), proving the screening scope is free-text-only.
    /// </summary>
    [Fact]
    public async Task Typify_ShouldNotScreenPhoneField_WhenDispositionIsAutoAi()
    {
        const string phoneFieldKey = "callback_phone";
        const string phoneValue = "+1 555 123 4567";

        // Seed a schema directly so it carries a Phone-type field (the admin DTO path is
        // not needed; this keeps the field-type assertion focused).
        var schemaId = EntityId.New();
        using (var scope = _factory.Services.CreateScope())
        {
            var schemaStore = scope.ServiceProvider.GetRequiredService<ITypificationSchemaStore>();
            var bindingStore = scope.ServiceProvider.GetRequiredService<ISchemaBindingStore>();

            await schemaStore.SaveAsync(new TypificationSchema
            {
                SchemaId = schemaId,
                TenantId = s_tenantId,
                Name = "C1 Phone Not Screened",
                Version = 1,
                IsPublished = true,
                MaxDepth = 5,
                Nodes =
                [
                    new TypificationNode
                    {
                        NodeId = EntityId.From(RootNodeId),
                        ParentNodeId = null,
                        Label = "Root",
                        Code = RootCode,
                        SortOrder = 0,
                        IsLeaf = false,
                    },
                    new TypificationNode
                    {
                        NodeId = EntityId.From(LeafNodeId),
                        ParentNodeId = EntityId.From(RootNodeId),
                        Label = "Leaf",
                        Code = LeafCode,
                        SortOrder = 0,
                        IsLeaf = true,
                        Leaf = new LeafOutcome { Category = TypificationCategory.Success, IsActive = true },
                    },
                ],
                Fields =
                [
                    new TypificationField
                    {
                        FieldId = EntityId.New(),
                        Key = phoneFieldKey,
                        Label = "Callback Phone",
                        Type = FieldType.Phone,
                        Required = false,
                        SortOrder = 0,
                    },
                ],
                DataDips = [],
                AiConfig = new TypificationAiConfig { Enabled = false, SentimentGating = false, EntityFieldMap = new Dictionary<string, string>() },
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);

            await bindingStore.SaveAsync(new SchemaBinding
            {
                BindingId = EntityId.New(),
                TenantId = s_tenantId,
                Scope = BindingScope.Tenant,
                ScopeRef = null,
                SchemaId = schemaId,
                SubTreeRootNodeId = null,
                Priority = 7,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        var conv = await SeedConversationAsync();

        // Suggestion matches the committed leaf → Source=AutoAi → PII screen runs.
        using (var scope = _factory.Services.CreateScope())
        {
            var suggestionStore = scope.ServiceProvider.GetRequiredService<IAiSuggestionStore>();
            await suggestionStore.SaveAsync(new AiSuggestionRecord
            {
                Id = EntityId.New(),
                TenantId = EntityId.From(AuthenticatedPlatformApiFactory.TestTenantId),
                ConversationId = conv.ConversationId,
                SchemaId = schemaId,
                SchemaVersion = 1,
                SuggestedLeafNodeId = EntityId.From(LeafNodeId),
                SuggestedNodePath = [RootNodeId, LeafNodeId],
                SuggestedFieldValues = new Dictionary<string, string>(),
                Confidence = 0.9,
                ModelId = "gpt-4o-mini",
                PromptVersion = "v1",
                SurfacedBand = TypificationBand.Suggest,
                CreatedAt = DateTimeOffset.UtcNow,
            }, CancellationToken.None);
        }

        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string> { [phoneFieldKey] = phoneValue },
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope2 = _factory.Services.CreateScope();
        var store = scope2.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var saved = await store.GetByConversationIdAsync(s_tenantId, conv.ConversationId, CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.Source.Should().Be(SubmissionSource.AutoAi);

        // Phone is NOT free-text → persisted raw (NOT masked to [PHONE]).
        saved.FieldValues[phoneFieldKey].Should().Be(phoneValue);
        saved.FieldValues[phoneFieldKey].Should().NotContain("[PHONE]");
    }

    /// <summary>
    /// Pure manual typify (no stored AI suggestion) → NO audit entry for
    /// "typification.ai_disposition" is written.
    /// </summary>
    [Fact]
    public async Task Typify_ShouldNotWriteAiDispositionAudit_WhenNoStoredSuggestion()
    {
        await SeedPublishedTenantSchemaAsync("B4b Audit No Suggestion");
        var conv = await SeedConversationAsync();

        // No suggestion seeded — purely manual typify.
        var body = JsonContent.Create(new
        {
            selectedNodePath = new[] { RootNodeId, LeafNodeId },
            fieldValues = new Dictionary<string, string> { [RequiredFieldKey] = "manual no audit" },
        });

        var response = await _client.PostAsync($"/api/conversations/{conv.ConversationId.Value}/typify", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var auditScope = _factory.Services.CreateScope();
        var auditStore = auditScope.ServiceProvider.GetRequiredService<Verbara.Platform.Audit.IAuditStore>();
        var hits = await auditStore.SearchAsync(
            s_tenantId,
            new Verbara.Platform.Audit.AuditQuery(Action: "typification.ai_disposition", Page: 1, PageSize: 50),
            CancellationToken.None);

        // The conversation has no audit entry — no AI suggestion was involved.
        hits.Items.Should().NotContain(e => e.TargetId == conv.ConversationId.Value);
    }
}
