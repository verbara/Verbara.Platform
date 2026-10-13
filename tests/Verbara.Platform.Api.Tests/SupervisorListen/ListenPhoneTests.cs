using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Pro.MultiTenant;
using H = Verbara.Platform.Api.Tests.SupervisorListen.ListenHarness;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 2.6 (design D8): the admin-set listen phone (option C) — AdminOnly, strict E.164, audited
/// with the last four digits only — and a phone listen that no licence bit gates.
/// </summary>
public sealed class ListenPhoneTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private const string Fixture = "supervisor-listen-phone.v1.json";
    private readonly AuthenticatedPlatformApiFactory _factory;

    public ListenPhoneTests(AuthenticatedPlatformApiFactory factory) => _factory = factory;

    private static string Path(string userId) => $"/api/v1/admin/users/{userId}/listen-phone";

    private static async Task<JsonObject> Body(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    [Fact]
    public async Task Put_ShouldStoreAnE164Number_AndGetShouldReadIt_InTheFrozenShape()
    {
        var userId = _factory.SeedActiveUser();
        using var client = _factory.CreateAuthenticatedClient();

        var put = await client.PutAsJsonAsync(Path(userId), new { phoneNumber = " +573001234567 " });
        var get = await client.GetAsync(Path(userId));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = SupervisorListenTestKit.FixtureBody(Fixture, "get");
        expected["userId"] = userId;
        SupervisorListenTestKit.AssertSame(expected, await Body(put));
        SupervisorListenTestKit.AssertSame(expected, await Body(get));
    }

    [Theory]
    [InlineData("573001234567")]
    [InlineData("300 123 4567")]
    [InlineData("+0123456")]
    [InlineData("+1234567890123456")]
    [InlineData("+57 300 123 4567")]
    [InlineData("+12345")]
    public async Task Put_ShouldRefuseANonE164Number_EchoingIt_AndStoreNothing(string value)
    {
        var userId = _factory.SeedActiveUser();
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.PutAsJsonAsync(Path(userId), new { phoneNumber = value });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var expected = SupervisorListenTestKit.FixtureBody(Fixture, "invalid");
        expected["phoneNumber"] = value;
        SupervisorListenTestKit.AssertSame(expected, await Body(response));
        (await Body(await client.GetAsync(Path(userId))))["phoneNumber"].Should().BeNull("nothing is stored");
    }

    [Fact]
    public async Task Put_ShouldClearTheNumber_WhenNull()
    {
        var userId = _factory.SeedActiveUser();
        using var client = _factory.CreateAuthenticatedClient();
        await client.PutAsJsonAsync(Path(userId), new { phoneNumber = "+573001234567" });

        var response = await client.PutAsJsonAsync(Path(userId), new { phoneNumber = (string?)null });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Body(response);
        body.ContainsKey("phoneNumber").Should().BeTrue();
        body["phoneNumber"].Should().BeNull();
    }

    [Fact]
    public async Task GetAndPut_ShouldAnswer404_ForAUserOutsideTheTenant()
    {
        using var client = _factory.CreateAuthenticatedClient();

        (await client.GetAsync(Path("no-such-user"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PutAsJsonAsync(Path("no-such-user"), new { phoneNumber = "+573001234567" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_ShouldAuditTheChange_WithTheLastFourDigitsOnly()
    {
        var userId = _factory.SeedActiveUser();
        using var client = _factory.CreateAuthenticatedClient();

        await client.PutAsJsonAsync(Path(userId), new { phoneNumber = "+573001234567" });

        var audit = await _factory.Services.GetRequiredService<IAuditStore>().SearchAsync(
            new TenantId(AuthenticatedPlatformApiFactory.TestTenantId), new AuditQuery(Action: "user.listen_phone_set"),
            CancellationToken.None);
        var entry = audit.Items.Should().ContainSingle(e => e.TargetId == userId).Subject;
        entry.Metadata!["phone_last4"].Should().Be("4567");
        entry.Metadata.Values.Should().NotContain(v => v.Contains("3001234567"), "the full number is never written");
    }

    [Fact]
    public async Task Get_ShouldNeverCarryTheListenEndpointSecret()
    {
        var userId = _factory.SeedActiveUser();
        await _factory.Services.GetRequiredService<ISupervisorListenProfileStore>().SaveAsync(new SupervisorListenProfile
        {
            TenantId = new TenantId(AuthenticatedPlatformApiFactory.TestTenantId), UserId = EntityId.From(userId),
            SipExtension = "sl12345678", SipPassword = "super-secret-listen-password", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        using var client = _factory.CreateAuthenticatedClient();

        var raw = await (await client.GetAsync(Path(userId))).Content.ReadAsStringAsync();
        var users = await (await client.GetAsync("/api/v1/admin/users")).Content.ReadAsStringAsync();

        raw.Should().NotContain("super-secret-listen-password");
        users.Should().NotContain("super-secret-listen-password");
    }
}

/// <summary>supervisor-silent-listen 2.6: a supervisor cannot set their own listen number.</summary>
public sealed class ListenPhoneSelfServiceTests : IClassFixture<SupervisorListenApiFactory>
{
    private readonly SupervisorListenApiFactory _factory;

    public ListenPhoneSelfServiceTests(SupervisorListenApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Put_ShouldBeForbidden_ForASupervisorSettingTheirOwnNumber()
    {
        using var client = _factory.CreateSupervisorClient();

        var response = await client.PutAsJsonAsync(
            $"/api/v1/admin/users/{SupervisorListenApiFactory.PermittedUserId}/listen-phone", new { phoneNumber = "+15550001111" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.Services.GetRequiredService<ISupervisorListenProfileStore>()
            .GetAsync(H.TenantId, H.Supervisor, CancellationToken.None))?.PhoneNumber.Should().BeNull();
    }
}

/// <summary>supervisor-silent-listen 2.6 (owner decision 0.2): no licence bit gates a phone listen.</summary>
public sealed class ListenPhoneLicenceTests
{
    [Fact]
    public async Task PhoneListen_ShouldNotBeRefused_WhenEveryLicenceFeatureIsDenied()
    {
        var h = new H();
        var call = h.SeedVoiceCall();
        await h.SeedPhoneAsync("+573001234567");
        var ami = Substitute.For<IAmiConnection>();
        ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>()).Returns(new ManagerResponse { Response = "Success" });
        await using var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        pool.AddExistingServer("primary", new VerbaraServer(ami, NullLogger<VerbaraServer>.Instance));
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(new LicenseGuardResult(false, LicenseBlockReason.NotLicensed));

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(pool);
        services.AddSingleton(guard);
        services.AddSingleton(h.Conversations);
        services.AddSingleton(h.Profiles);
        services.AddSingleton(h.Tenants);
        services.AddSingleton(h.Audit);
        services.AddSingleton<IClock>(h.Clock);
        services.AddSingleton<IVoiceCallLegs>(h.CallLegs);
        services.AddKeyedSingleton<IClusterLeader>(VoiceLeaderResources.AmiOwner, (_, _) => new AlwaysLeader(VoiceLeaderResources.AmiOwner));
        services.TryAddSingleton<OriginateExecutorBase>(sp => new DefaultOriginateExecutor(
            sp.GetRequiredService<VerbaraServerPool>(), sp.GetRequiredService<ILogger<DefaultOriginateExecutor>>(),
            licenseGuard: sp.GetService<ILicenseGuard>()));
        services.AddSupervisorListen(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Asterisk:Outbound:DefaultTrunk"] = "pstn-default" })
            .Build());
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<SupervisorListenService>()
            .StartAsync(H.Caller(), H.TenantId, H.Supervisor, call.ConversationId.Value, "phone", CancellationToken.None);

        result.Refusal.Should().BeNull("supervision consumes no licence, on either destination (PDR-0003 §C.6)");
        result.Listen!.Destination.Should().Be("phone");
        guard.DidNotReceive().CanExecute(Arg.Any<LicenseFeature>());
    }
}
