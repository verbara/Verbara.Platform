using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
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
                ConfidenceThreshold = 0,
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
        // A schema field with PrefillSource{Kind:Metadata, Ref:"patientId"} — only
        // expressible via direct store seeding (the admin DTO drops PrefillSource).
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
                        ConfidenceThreshold = 0,
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
}
