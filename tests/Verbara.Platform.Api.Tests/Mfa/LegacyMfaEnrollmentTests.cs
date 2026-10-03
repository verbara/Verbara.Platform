using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions.Execution;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.Auth;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OtpNet;

namespace Verbara.Platform.Api.Tests.Mfa;

/// <summary>
/// The legacy enrollment surface, <c>POST /auth/mfa/setup</c> and <c>POST /auth/mfa/confirm</c>,
/// against an account whose MFA is already on. Both take only an access token, so neither may
/// replace or re-confirm an enrolled factor: re-enrolling goes through <c>DELETE /auth/mfa</c>
/// first, which asks for the password. The profile wizard has refused this from the start.
/// </summary>
public sealed class LegacyMfaEnrollmentTests : IClassFixture<AccountStatusApiFactory>
{
    private const string Customer = AccountStatusApiFactory.CustomerTenantId;
    private const string EnrolledRecoveryCode = "ENROLLEDCODE0001";

    private readonly AccountStatusApiFactory _factory;

    public LegacyMfaEnrollmentTests(AccountStatusApiFactory factory) => _factory = factory;

    [Fact]
    public async Task MfaSetup_ShouldReturn400AndKeepTheFactor_WhenMfaIsAlreadyEnabled()
    {
        var secret = NewSecret();
        var user = SeedUser(enrolledSecret: secret);
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(user));

        var response = await client.PostAsync("/api/v1/auth/mfa/setup", null);

        var stored = _factory.GetUser(user.UserId.Value, Customer)!;
        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                because: "an access token alone must not replace an enrolled second factor");
            (await response.Content.ReadAsStringAsync()).Should().ContainEquivalentOf("already enrolled");
            stored.MfaEnabled.Should().BeTrue();
            stored.MfaSecret.Should().Be(secret, because: "the enrolled secret stays the account's secret");
            RecoveryCodeIsValid(stored, EnrolledRecoveryCode).Should().BeTrue(
                because: "the enrolled recovery codes stay valid");
        }
    }

    [Fact]
    public async Task MfaConfirm_ShouldReturn400AndKeepTheConfirmation_WhenMfaIsAlreadyEnabled()
    {
        var secret = NewSecret();
        var user = SeedUser(enrolledSecret: secret);
        var confirmedAt = _factory.GetUser(user.UserId.Value, Customer)!.MfaConfirmedAt;
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(user));

        var response = await client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = TotpNow(secret) });

        var stored = _factory.GetUser(user.UserId.Value, Customer)!;
        using (new AssertionScope())
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                because: "confirming is part of enrolling, which an enrolled account cannot do again");
            (await response.Content.ReadAsStringAsync()).Should().ContainEquivalentOf("already enrolled");
            stored.MfaConfirmedAt.Should().Be(confirmedAt);
        }
    }

    [Fact]
    public async Task MfaSetup_ShouldReturnTheNewSecret_WhenMfaIsNotEnabled()
    {
        var user = SeedUser(enrolledSecret: null);
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(user));

        var response = await client.PostAsync("/api/v1/auth/mfa/setup", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var secret = await ReadSecretAsync(response);
        var stored = _factory.GetUser(user.UserId.Value, Customer)!;
        stored.MfaSecret.Should().Be(secret, because: "setup stores the pending secret /mfa/confirm checks against");
        stored.MfaEnabled.Should().BeFalse(because: "MFA is on only once the code is confirmed");
    }

    [Fact]
    public async Task MfaConfirm_ShouldEnableMfa_WhenSetupWasStartedAndTheCodeIsValid()
    {
        var user = SeedUser(enrolledSecret: null);
        using var client = _factory.CreateBearerClient(_factory.MintAccessToken(user));
        var secret = await ReadSecretAsync(await client.PostAsync("/api/v1/auth/mfa/setup", null));

        var response = await client.PostAsJsonAsync("/api/v1/auth/mfa/confirm", new { code = TotpNow(secret) });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.GetUser(user.UserId.Value, Customer)!;
        stored.MfaEnabled.Should().BeTrue();
        stored.MfaConfirmedAt.Should().NotBeNull();
    }

    // ─── The handlers refuse before writing anything ─────────────────────────

    [Fact]
    public async Task MfaSetup_ShouldNotWriteToTheStore_WhenMfaIsAlreadyEnabled()
    {
        var (store, context) = HandlerWithEnrolledUser();

        var result = await AuthEndpoints.MfaSetup(context, store, CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>()
            .Which.Value!.Error.Should().Be(AuthEndpoints.MfaAlreadyEnrolledMessage);
        await store.DidNotReceiveWithAnyArgs().SetPendingMfaAsync(default, default, default!, default!, default, default);
    }

    [Fact]
    public async Task MfaConfirm_ShouldNotWriteToTheStore_WhenMfaIsAlreadyEnabled()
    {
        var (store, context) = HandlerWithEnrolledUser();

        var result = await AuthEndpoints.MfaConfirm(
            new MfaConfirmRequest(TotpNow(HandlerSecret)), context, store,
            new AuthEventService(Substitute.For<IAuthEventStore>()), Substitute.For<INotificationService>(),
            CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>()
            .Which.Value!.Error.Should().Be(AuthEndpoints.MfaAlreadyEnrolledMessage);
        await store.DidNotReceiveWithAnyArgs().EnableMfaAsync(default, default, default, default);
    }

    private const string HandlerSecret = "JBSWY3DPEHPK3PXP";

    private static (IUserStore Store, DefaultHttpContext Context) HandlerWithEnrolledUser()
    {
        var store = Substitute.For<IUserStore>();
        store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>()).Returns(new User
        {
            UserId = EntityId.From("enrolled-user"),
            TenantId = new TenantId(Customer),
            Email = "enrolled-user@acct-status.test",
            DisplayName = "Enrolled",
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            MfaEnabled = true,
            MfaSecret = HandlerSecret,
            MfaConfirmedAt = DateTimeOffset.UtcNow.AddDays(-30),
        });
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", Customer), new Claim("sub", "enrolled-user")], "test")),
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        return (store, context);
    }

    private User SeedUser(string? enrolledSecret)
    {
        var id = $"legacy-mfa-{Guid.NewGuid():N}";
        var enrolled = enrolledSecret is not null;
        return _factory.SeedUser(new User
        {
            UserId = EntityId.From(id),
            TenantId = new TenantId(Customer),
            Email = $"{id}@acct-status.test",
            DisplayName = id,
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            PasswordHash = PasswordService.HashPassword("Legacy-Passw0rd!"),
            MfaEnabled = enrolled,
            MfaSecret = enrolledSecret,
            MfaRecoveryCodes = enrolled ? MfaService.HashRecoveryCodes([EnrolledRecoveryCode]).ToList() : null,
            MfaConfirmedAt = enrolled ? DateTimeOffset.UtcNow.AddDays(-30) : null,
        });
    }

    private bool RecoveryCodeIsValid(User user, string code) =>
        user.MfaRecoveryCodes is { Count: > 0 } codes
        && MfaService.ValidateRecoveryCode(
            _factory.Services.GetRequiredService<IRecoveryCodeService>(), code, codes, user.UserId.Value).IsValid;

    private static string NewSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    private static string TotpNow(string secret) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow);

    private static async Task<string> ReadSecretAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("secret").GetString()!;
    }
}
