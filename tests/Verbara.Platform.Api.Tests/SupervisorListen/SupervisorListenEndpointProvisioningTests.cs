using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Models;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 2.4 (design D3): the supervisor's receive-only listen endpoint — minted once, provisioned
/// through the existing desired-state path as a reserved <c>sup-{userId}</c> slot on a per-tenant
/// <c>supervisor-listen</c> profile, never an agent, and removed with its user.
/// </summary>
public sealed partial class SupervisorListenEndpointProvisioningTests
{
    private const string Tenant = "acme";
    private static readonly TenantId TenantId = new(Tenant);
    private static readonly EntityId Supervisor = EntityId.From("7d2c9e4b1a5f4c3e8b6d0f1a2c3e4b5d");

    private readonly InMemorySupervisorListenProfileStore _profiles = new();
    private readonly IUserStore _users = Substitute.For<IUserStore>();
    private readonly IRealtimeSyncService _sync = Substitute.For<IRealtimeSyncService>();
    private readonly EndpointProfileStoreBase _endpointProfiles = Substitute.For<EndpointProfileStoreBase>();
    private readonly List<EndpointProfile> _createdProfiles = [];

    public SupervisorListenEndpointProvisioningTests()
    {
        _users.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(ci => new User
            {
                UserId = ci.ArgAt<EntityId>(1), TenantId = ci.ArgAt<TenantId>(0), Email = "ana@acme.test",
                DisplayName = "Ana Supervisor", Role = UserRole.Supervisor, Status = UserStatus.Active, CreatedAt = DateTimeOffset.UnixEpoch,
            });
        _endpointProfiles.GetDefaultAsync(Arg.Any<string>(), EndpointProfileType.Agent, Arg.Any<CancellationToken>())
            .Returns(new EndpointProfile
            {
                Id = 1, TenantId = Tenant, Name = "WebRTC Agent", Type = EndpointProfileType.Agent, IsDefault = true,
                Transport = "transport-wss", Codecs = "opus,ulaw", Webrtc = true, MaxContacts = 3,
                DtlsAutoGenerateCert = true, DirectMedia = false, Context = "from-internal", QualifyFrequency = 25,
            });
        _endpointProfiles.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<EndpointProfile>)[.. _createdProfiles]);
        _endpointProfiles.CreateAsync(Arg.Any<EndpointProfile>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var profile = ci.ArgAt<EndpointProfile>(0);
                profile.Id = 42;
                _createdProfiles.Add(profile);
                return 42L;
            });
    }

    private SupervisorListenEndpointProvisioner Provisioner(EndpointProfileStoreBase? endpointProfiles = null, bool noStore = false) =>
        new(_profiles, _users, _sync, noStore ? null : endpointProfiles ?? _endpointProfiles,
            new ListenHarness.MutableClock(DateTimeOffset.UnixEpoch), NullLogger<SupervisorListenEndpointProvisioner>.Instance);

    [GeneratedRegex("^acme-sl[0-9a-f]{8}$")]
    private static partial Regex AuthUsername();

    [GeneratedRegex("^[A-Za-z0-9_-]{32}$")]
    private static partial Regex Base64Url32();

    [Fact]
    public async Task Provision_ShouldMintCredentialsOnce_AndReturnTheSameBodyLater()
    {
        var first = await Provisioner().ProvisionAsync(TenantId, Supervisor, CancellationToken.None);
        var second = await Provisioner().ProvisionAsync(TenantId, Supervisor, CancellationToken.None);

        first.Should().NotBeNull();
        first!.EndpointId.Should().Be($"acme-agent-sup-{Supervisor.Value}");
        first.AuthorizationUsername.Should().MatchRegex(AuthUsername().ToString());
        first.SipPassword.Should().MatchRegex(Base64Url32().ToString(), "24 CSPRNG bytes, base64url");
        first.DisplayName.Should().Be("Ana Supervisor");
        first.ListenCallerName.Should().Be("Verbara Listen");
        second.Should().Be(first, "later calls return the same credentials");
    }

    [Fact]
    public async Task Provision_ShouldMintDifferentSecrets_ForDifferentSupervisors()
    {
        var a = await Provisioner().ProvisionAsync(TenantId, Supervisor, CancellationToken.None);
        var b = await Provisioner().ProvisionAsync(TenantId, EntityId.From("another-supervisor"), CancellationToken.None);

        b!.SipPassword.Should().NotBe(a!.SipPassword);
        b.AuthorizationUsername.Should().NotBe(a.AuthorizationUsername);
    }

    [Fact]
    public async Task Provision_ShouldCreateTheListenProfileOncePerTenant_AsACopyOfTheDefaultAgentProfile()
    {
        await Provisioner().ProvisionAsync(TenantId, Supervisor, CancellationToken.None);
        await Provisioner().ProvisionAsync(TenantId, EntityId.From("another-supervisor"), CancellationToken.None);

        var profile = _createdProfiles.Should().ContainSingle().Subject;
        profile.Name.Should().Be("supervisor-listen");
        profile.Type.Should().Be(EndpointProfileType.Agent);
        profile.IsDefault.Should().BeFalse("it must never become the tenant's default agent profile");
        profile.Context.Should().Be("listen-only", "a dead-end context: an INVITE from the endpoint finds nothing");
        profile.MaxContacts.Should().Be(1, "one registration; the latest REGISTER wins");
        profile.Transport.Should().Be("transport-wss");
        profile.Codecs.Should().Be("opus,ulaw");
        profile.Webrtc.Should().BeTrue();
        profile.DtlsAutoGenerateCert.Should().BeTrue();
        profile.QualifyFrequency.Should().Be(25);
    }

    [Fact]
    public async Task Provision_ShouldSyncTheSlotRightAway_OnTheListenProfile()
    {
        var body = await Provisioner().ProvisionAsync(TenantId, Supervisor, CancellationToken.None);

        var extension = body!.AuthorizationUsername["acme-".Length..];
        await _sync.Received(1).SyncAgentAsync(
            Tenant, $"sup-{Supervisor.Value}", "Ana Supervisor", extension, body.SipPassword, 42L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Provision_ShouldStillAnswer_WhenTheImmediateSyncFails()
    {
        _sync.SyncAgentAsync(default!, default!, default!, default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("realtime db down"));

        var body = await Provisioner().ProvisionAsync(TenantId, Supervisor, CancellationToken.None);

        body.Should().NotBeNull("the next reconcile converges; a failed immediate sync is logged, not thrown");
    }

    [Fact]
    public async Task Provision_ShouldRefuse_WhenTheTenantIdIsTooLongForTheSipUsername()
    {
        var longTenant = new TenantId(new string('t', 30));   // 30 + "-sl" + 8 = 41 > 40

        var body = await Provisioner().ProvisionAsync(longTenant, Supervisor, CancellationToken.None);

        body.Should().BeNull();
        (await _profiles.GetAsync(longTenant, Supervisor, CancellationToken.None)).Should().BeNull("no credentials are minted");
        await _sync.DidNotReceiveWithAnyArgs().SyncAgentAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Provision_ShouldAccept_ATwentyNineCharacterTenant()
    {
        var body = await Provisioner().ProvisionAsync(new TenantId(new string('t', 29)), Supervisor, CancellationToken.None);

        body!.AuthorizationUsername.Length.Should().Be(40);
    }

    [Fact]
    public async Task Provision_ShouldRefuse_WhenNoRealtimeProfileStoreIsConfigured()
    {
        var body = await Provisioner(noStore: true).ProvisionAsync(TenantId, Supervisor, CancellationToken.None);

        body.Should().BeNull("without the listen-only profile the endpoint would inherit a dialable context");
        (await _profiles.GetAsync(TenantId, Supervisor, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Provision_ShouldKeepTheStoredListenPhone()
    {
        await _profiles.SaveAsync(new SupervisorListenProfile
        {
            TenantId = TenantId, UserId = Supervisor, PhoneNumber = "+573001234567",
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);

        await Provisioner().ProvisionAsync(TenantId, Supervisor, CancellationToken.None);

        (await _profiles.GetAsync(TenantId, Supervisor, CancellationToken.None))!.PhoneNumber.Should().Be("+573001234567");
    }

    // ─── The desired state ──────────────────────────────────────────────────────

    private PlatformDesiredStateProvider Provider(FakeAgentAccountStatusLookup status, IAgentStore? agents = null)
    {
        var agentStore = agents ?? Substitute.For<IAgentStore>();
        if (agents is null)
            agentStore.ListAsync(Arg.Any<TenantId>(), Arg.Any<AgentQuery>(), Arg.Any<CancellationToken>())
                .Returns(PagedResult<Agent>.Empty(1, 1000));
        var queueStore = Substitute.For<IQueueStore>();
        var membership = new QueueMembershipService(queueStore, agentStore, new Verbara.Sdk.Pro.Routing.Skills.InMemorySkillCatalog(),
            Substitute.For<IQueueMembershipStore>(), NullLogger<QueueMembershipService>.Instance);
        return new PlatformDesiredStateProvider(
            new ConfigurationBuilder().Build(), agentStore, queueStore, Substitute.For<TrunkStoreBase>(), membership, status,
            _profiles, _endpointProfiles, _users);
    }

    [Fact]
    public async Task ExpectedAgents_ShouldAddOneSlotPerActiveUserWithCredentials()
    {
        await Provisioner().ProvisionAsync(TenantId, Supervisor, CancellationToken.None);
        var inactive = EntityId.From("inactive-supervisor");
        await Provisioner().ProvisionAsync(TenantId, inactive, CancellationToken.None);
        var phoneOnly = EntityId.From("phone-only");
        await _profiles.SaveAsync(new SupervisorListenProfile
        {
            TenantId = TenantId, UserId = phoneOnly, PhoneNumber = "+573001234567",
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);
        var status = new FakeAgentAccountStatusLookup();
        status.Inactive.Add(inactive);

        var expected = await Provider(status).GetExpectedAgentsAsync(Tenant);

        var slot = expected.Should().ContainSingle().Subject;
        var stored = await _profiles.GetAsync(TenantId, Supervisor, CancellationToken.None);
        slot.AgentId.Should().Be($"sup-{Supervisor.Value}");
        slot.DisplayName.Should().Be("Ana Supervisor");
        slot.Extension.Should().Be(stored!.SipExtension);
        slot.SipPassword.Should().Be(stored.SipPassword);
        slot.ProfileId.Should().Be(42L);
    }

    [Fact]
    public async Task ExpectedAgents_ShouldListNoSlot_WhenTheTenantHasNoListenProfile()
    {
        await Provisioner().ProvisionAsync(TenantId, Supervisor, CancellationToken.None);
        _createdProfiles.Clear();   // the listen profile was deleted by hand

        var expected = await Provider(new FakeAgentAccountStatusLookup()).GetExpectedAgentsAsync(Tenant);

        expected.Should().BeEmpty("without its listen-only profile the endpoint would get the default, dialable context");
    }

    // ─── Removed with its user ──────────────────────────────────────────────────

    [Fact]
    public async Task GdprPurge_ShouldDeleteTheListenProfile()
    {
        await _profiles.SaveAsync(new SupervisorListenProfile
        {
            TenantId = TenantId, UserId = Supervisor, SipExtension = "sl00000000", SipPassword = "pw", PhoneNumber = "+573001234567",
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        }, CancellationToken.None);
        var service = new GdprPurgeService(
            new InMemoryContactStore(), new InMemoryConversationStore(), new InMemoryMessageStore(),
            new InMemoryAuthEventStore(), new InMemoryUserStore(), new InMemoryRefreshTokenStore(), new InMemoryPurgeLogStore(),
            new InMemoryAuditStore(), new InMemoryAgentStore(), new InMemoryQueueMembershipStore(), Substitute.For<IAuditService>(),
            Substitute.For<ILicensedUserChangeWriter>(), NullLogger<GdprPurgeService>.Instance, _profiles);

        await service.PurgeUserDataAsync(Tenant, Supervisor.Value, "admin", "erasure", CancellationToken.None);

        (await _profiles.GetAsync(TenantId, Supervisor, CancellationToken.None)).Should().BeNull();
    }
}

/// <summary>supervisor-silent-listen 2.4: the admin user delete removes the user's listen profile with the user.</summary>
public sealed class SupervisorListenAdminDeleteTests : IClassFixture<Verbara.Platform.Api.Tests.Auth.AccountStatusApiFactory>
{
    private static readonly TenantId Tenant = new(Verbara.Platform.Api.Tests.Auth.AccountStatusApiFactory.CustomerTenantId);
    private readonly Verbara.Platform.Api.Tests.Auth.AccountStatusApiFactory _factory;

    public SupervisorListenAdminDeleteTests(Verbara.Platform.Api.Tests.Auth.AccountStatusApiFactory factory) => _factory = factory;

    [Fact]
    public async Task DeleteUser_ShouldDeleteTheUsersListenProfile()
    {
        var user = _factory.SaveUser($"sl-user-{Guid.NewGuid():N}", Tenant.Value, UserRole.Supervisor, UserStatus.Active);
        var profiles = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<ISupervisorListenProfileStore>(_factory.Services);
        await profiles.SaveAsync(new SupervisorListenProfile
        {
            TenantId = Tenant, UserId = user.UserId, SipExtension = $"sl{Random.Shared.Next():x8}", SipPassword = "pw",
            PhoneNumber = "+573001234567", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        var admin = _factory.GetUser(Verbara.Platform.Api.Tests.Auth.AccountStatusApiFactory.CustomerAdminUserId, Tenant.Value)!;
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(admin));

        var response = await client.DeleteAsync($"/api/v1/admin/users/{user.UserId.Value}");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);
        (await profiles.GetAsync(Tenant, user.UserId, CancellationToken.None)).Should().BeNull(
            "the listen phone is personal data and the endpoint must leave the desired state with its user");
    }
}
