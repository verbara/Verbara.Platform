using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// ADR-0034 task group 6 — API tests for the per-tenant autonomous-disposition activation gate
/// (POST/DELETE /admin/typification/autonomous-disposition) and the supervisor correction endpoint
/// (POST /conversations/{id}/typification-correction). The correction tests prove the SEPARATE
/// append-only correction record holds the human path while the original AutoAi submission's leaf,
/// path, and Source stay byte-identical (only CorrectionState/CorrectedAt flip).
/// </summary>
public sealed class AutonomousDispositionEndpointTests
{
    private const string TenantId = AuthenticatedPlatformApiFactory.TestTenantId;

    // Hoisted per CA1861 (constant array args in repeated JSON payloads).
    private static readonly string[] HumanPath = ["root-human", "leaf-human"];
    private static readonly string[] HumanPath2 = ["root-human2", "leaf-human2"];

    // ─── Activation gate: POST/DELETE ─────────────────────────────────────────

    [Fact]
    public async Task ActivateGate_ShouldReturn201AndPersistActivation_WhenAdmin()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/api/admin/typification/autonomous-disposition",
            JsonContent.Create(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        body!["active"]!.GetValue<bool>().Should().BeTrue();
        body["attestedByUserId"]!.GetValue<string>().Should().Be(AuthenticatedPlatformApiFactory.TestUserId);

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantAutonomousDispositionStore>();
        var record = await store.GetAsync(new TenantId(TenantId), CancellationToken.None);
        record.Should().NotBeNull();
        record!.IsActive.Should().BeTrue();
        record.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task ActivateGate_ShouldClearPriorRevocation_WhenReactivated()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        var client = factory.CreateAuthenticatedClient();

        // Activate, revoke, then re-activate — the fresh record must clear the revocation
        // (B1 review: a stale revoked record fed to Upsert would leave revoked_at set).
        (await client.PostAsync("/api/admin/typification/autonomous-disposition",
            JsonContent.Create(new { }))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.DeleteAsync("/api/admin/typification/autonomous-disposition"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await client.PostAsync(
            "/api/admin/typification/autonomous-disposition", JsonContent.Create(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantAutonomousDispositionStore>();
        var record = await store.GetAsync(new TenantId(TenantId), CancellationToken.None);
        record!.IsActive.Should().BeTrue(because: "re-activation constructs a fresh non-revoked record");
        record.RevokedAt.Should().BeNull();
        record.RevokedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task RevokeGate_ShouldReturn204AndSoftDelete_WhenAdmin()
    {
        using var factory = new AuthenticatedPlatformApiFactory();
        var client = factory.CreateAuthenticatedClient();
        (await client.PostAsync("/api/admin/typification/autonomous-disposition",
            JsonContent.Create(new { }))).StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await client.DeleteAsync("/api/admin/typification/autonomous-disposition");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantAutonomousDispositionStore>();
        var record = await store.GetAsync(new TenantId(TenantId), CancellationToken.None);
        record!.IsActive.Should().BeFalse();
        record.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ActivateGate_ShouldReturn403_WhenNotAdmin()
    {
        using var factory = new NonAdminAuthenticatedApiFactory();
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/api/admin/typification/autonomous-disposition", JsonContent.Create(new { }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RevokeGate_ShouldReturn403_WhenNotAdmin()
    {
        using var factory = new NonAdminAuthenticatedApiFactory();
        var client = factory.CreateAuthenticatedClient();

        var response = await client.DeleteAsync("/api/admin/typification/autonomous-disposition");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── Supervisor correction ────────────────────────────────────────────────

    // A committed AutoAi submission for the conversation, completed `ageDays` ago.
    private static TypificationSubmission MakeAutoAiSubmission(
        string conversationId, int ageDays, SubmissionSource source = SubmissionSource.AutoAi) => new()
    {
        TenantId = new TenantId(TenantId),
        ConversationId = EntityId.From(conversationId),
        AgentId = EntityId.From("verbara:ai:autonomous-worker"),
        SchemaId = EntityId.From("schema-1"),
        SchemaVersion = 1,
        SelectedNodePath = [EntityId.From("root-ai"), EntityId.From("leaf-ai")],
        LeafNodeId = EntityId.From("leaf-ai"),
        FieldValues = new Dictionary<string, string>(),
        AiSuggested = true,
        AiConfidence = 0.97,
        AiAccepted = true,
        Source = source,
        Duration = TimeSpan.Zero,
        CompletedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
        AutonomousActorId = source == SubmissionSource.AutoAi ? "verbara:ai:autonomous-worker" : null,
    };

    private static async Task SeedSubmissionAsync(WebApplicationFactory<Program> factory, TypificationSubmission submission)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        await store.SaveAsync(submission, CancellationToken.None);
    }

    [Fact]
    public async Task CorrectTypification_ShouldPersistSeparateRecordAndLeaveOriginalUnchanged_WhenAutonomousWithinWindow()
    {
        using var factory = new CorrectionPermissionFactory();
        var client = factory.CreateAuthenticatedClient();
        const string convId = "conv-correct-happy";
        await SeedSubmissionAsync(factory, MakeAutoAiSubmission(convId, ageDays: 1));

        var response = await client.PostAsync(
            $"/api/conversations/{convId}/typification-correction",
            JsonContent.Create(new { correctedNodePath = HumanPath }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        body!["correctedLeafNodeId"]!.GetValue<string>().Should().Be("leaf-human");
        body["confirmed"]!.GetValue<bool>().Should().BeFalse(because: "the human path differs from the AI path");

        using var scope = factory.Services.CreateScope();

        // The SEPARATE correction record holds the human path.
        var correctionStore = scope.ServiceProvider.GetRequiredService<ITypificationSubmissionCorrectionStore>();
        var correction = await correctionStore.GetAsync(new TenantId(TenantId), EntityId.From(convId), CancellationToken.None);
        correction.Should().NotBeNull();
        correction!.CorrectedLeafNodeId.Value.Should().Be("leaf-human");
        correction.CorrectedNodePath.Select(n => n.Value).Should().Equal("root-human", "leaf-human");
        correction.CorrectedByUserId.Should().Be(CorrectionPermissionFactory.TestUserId);

        // The ORIGINAL AutoAi submission's AI disposition is byte-identical; only status pointers flip.
        var submissionStore = scope.ServiceProvider.GetRequiredService<ITypificationSubmissionStore>();
        var original = await submissionStore.GetByConversationIdAsync(new TenantId(TenantId), EntityId.From(convId), CancellationToken.None);
        original!.LeafNodeId.Value.Should().Be("leaf-ai", because: "the AI leaf must NOT change");
        original.SelectedNodePath.Select(n => n.Value).Should().Equal("root-ai", "leaf-ai");
        original.Source.Should().Be(SubmissionSource.AutoAi);
        original.AiConfidence.Should().Be(0.97);
        original.CorrectionState.Should().Be(CorrectionState.Corrected, because: "the status pointer is flipped");
        original.CorrectedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CorrectTypification_ShouldReturn409NotAutonomous_WhenSubmissionIsManual()
    {
        using var factory = new CorrectionPermissionFactory();
        var client = factory.CreateAuthenticatedClient();
        const string convId = "conv-manual";
        await SeedSubmissionAsync(factory, MakeAutoAiSubmission(convId, ageDays: 1, source: SubmissionSource.Manual));

        var response = await client.PostAsync(
            $"/api/conversations/{convId}/typification-correction",
            JsonContent.Create(new { correctedNodePath = HumanPath }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        body!["code"]!.GetValue<string>().Should().Be("NotAutonomous");

        // No correction record written (guard ran before any write).
        using var scope = factory.Services.CreateScope();
        var correctionStore = scope.ServiceProvider.GetRequiredService<ITypificationSubmissionCorrectionStore>();
        (await correctionStore.GetAsync(new TenantId(TenantId), EntityId.From(convId), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task CorrectTypification_ShouldReturn409WindowExpired_WhenOlderThanWindow()
    {
        using var factory = new CorrectionPermissionFactory();
        var client = factory.CreateAuthenticatedClient();
        const string convId = "conv-expired";
        // Default AutonomousCorrectionWindowDays = 30; 40 days old is outside the window.
        await SeedSubmissionAsync(factory, MakeAutoAiSubmission(convId, ageDays: 40));

        var response = await client.PostAsync(
            $"/api/conversations/{convId}/typification-correction",
            JsonContent.Create(new { correctedNodePath = HumanPath }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        body!["code"]!.GetValue<string>().Should().Be("CorrectionWindowExpired");
    }

    [Fact]
    public async Task CorrectTypification_ShouldReturn409AlreadyCorrected_WhenCorrectedTwice()
    {
        using var factory = new CorrectionPermissionFactory();
        var client = factory.CreateAuthenticatedClient();
        const string convId = "conv-twice";
        await SeedSubmissionAsync(factory, MakeAutoAiSubmission(convId, ageDays: 1));

        var first = await client.PostAsync(
            $"/api/conversations/{convId}/typification-correction",
            JsonContent.Create(new { correctedNodePath = HumanPath }));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsync(
            $"/api/conversations/{convId}/typification-correction",
            JsonContent.Create(new { correctedNodePath = HumanPath2 }));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonNode.Parse(await second.Content.ReadAsStringAsync());
        body!["code"]!.GetValue<string>().Should().Be("AlreadyCorrected");
    }

    [Fact]
    public async Task CorrectTypification_ShouldReturn403_WhenCallerLacksPermission()
    {
        // NonAdmin (Agent role) with empty effective permissions → the
        // typification:correct-autonomous policy fails.
        using var factory = new NonAdminAuthenticatedApiFactory();
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync(
            "/api/conversations/any-conv/typification-correction",
            JsonContent.Create(new { correctedNodePath = HumanPath }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

/// <summary>
/// Variant of <see cref="AuthenticatedPlatformApiFactory"/> whose test caller is a non-admin
/// (Supervisor role, so it does NOT take the Admin permission short-circuit) that resolves the
/// <c>typification:correct-autonomous</c> permission ONLY for the owning user id. Exercises the real
/// PermissionResolver path for the correction endpoint.
/// </summary>
public sealed class CorrectionPermissionFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "correction-perm-key-77777";
    public const string TestTenantId = AuthenticatedPlatformApiFactory.TestTenantId;
    public const string TestUserId = "correction-supervisor";

    private static readonly string s_hashedKey = HashKey(TestApiKey);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            SetupSupervisorAuth(services, s_hashedKey, TestTenantId, TestUserId);
            AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);

            services.AddAllProFeaturesLicensed();
            if (!services.Any(d => d.ServiceType == typeof(byte[])))
                services.AddSingleton<byte[]>([]);

            AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);

            // Grant typification:correct-autonomous via IUserRoleStore for the owning user id only.
            var roleStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserRoleStore));
            if (roleStoreDescriptor is not null) services.Remove(roleStoreDescriptor);

            var roleStore = Substitute.For<IUserRoleStore>();
            var perms = new HashSet<string>(StringComparer.Ordinal) { "typification:correct-autonomous" };
            var empty = new HashSet<string>(StringComparer.Ordinal);
            roleStore.GetEffectivePermissionsAsync(
                    Arg.Any<TenantId>(),
                    Arg.Is<EntityId>(e => e.Value == TestUserId),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<string>>(perms));
            roleStore.GetEffectivePermissionsAsync(
                    Arg.Any<TenantId>(),
                    Arg.Is<EntityId>(e => e.Value != TestUserId),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlySet<string>>(empty));
            services.AddSingleton(roleStore);
        });

        var host = base.CreateHost(builder);
        AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(host.Services, TestTenantId);
        AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(host.Services, TestTenantId);
        return host;
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {TestApiKey}");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestTenantId);
        return client;
    }

    // A Supervisor-role API key + user: passes Authenticated (not Admin, so the permission
    // handler does not short-circuit) and resolves permissions via IUserRoleStore.
    private static void SetupSupervisorAuth(
        IServiceCollection services, string hashedKey, string tenantId, string userId)
    {
        var userEntityId = EntityId.From(userId);
        var tenantId_ = new TenantId(tenantId);

        var akDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IApiKeyStore));
        if (akDescriptor is not null) services.Remove(akDescriptor);

        var apiKeyStore = Substitute.For<IApiKeyStore>();
        var apiKey = new ApiKey
        {
            KeyId = EntityId.From("correction-key-id"),
            TenantId = tenantId_,
            Name = "Correction Supervisor Key",
            HashedKey = hashedKey,
            Scopes = ["*"],
            UserId = userEntityId,
            IsRevoked = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        apiKeyStore.GetByHashAsync(hashedKey, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<ApiKey?>(apiKey));
        services.AddSingleton(apiKeyStore);

        var userStoreDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IUserStore) && !d.IsKeyedService);
        if (userStoreDescriptor is not null) services.Remove(userStoreDescriptor);

        var userStore = Substitute.For<IUserStore>();
        var testUser = new User
        {
            UserId = userEntityId,
            TenantId = tenantId_,
            Email = "correction-supervisor@test.internal",
            DisplayName = "Correction Supervisor",
            Role = UserRole.Supervisor,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        userStore.GetByIdAsync(tenantId_, userEntityId, Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<User?>(testUser));
        userStore.ListAsync(Arg.Any<TenantId>(), Arg.Any<PagedQuery>(), Arg.Any<CancellationToken>())
                 .Returns(ci => Task.FromResult(PagedResult<User>.Empty(
                     ((PagedQuery)ci[1]).Page,
                     ((PagedQuery)ci[1]).PageSize)));
        services.AddSingleton(userStore);
    }

    private static string HashKey(string rawKey)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }
}
