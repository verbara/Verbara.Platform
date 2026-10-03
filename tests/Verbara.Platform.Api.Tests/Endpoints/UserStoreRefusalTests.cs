using System.Security.Claims;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Profile;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Mfa;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OtpNet;

namespace Verbara.Platform.Api.Tests.Endpoints;

/// <summary>
/// What each self-service handler answers when the store refuses its write because the account
/// changed after the handler read it: the password was replaced, MFA was enabled or disabled, the
/// user was deleted. The handler reports the refusal and announces nothing — no success event, no
/// notification — since nothing was written.
/// </summary>
/// <remarks>
/// Handlers are invoked directly (InternalsVisibleTo) over a substitute whose reads return the
/// account as it was and whose writes all refuse — the only way to put the concurrent change between
/// the read and the write deterministically.
/// </remarks>
public sealed class UserStoreRefusalTests
{
    private const string TenantId = "t-refusal";
    private const string UserId = "u-refusal";
    private const string Password = "Current-Passw0rd!";
    private const string Secret = "JBSWY3DPEHPK3PXP";

    private static readonly string s_passwordHash = PasswordService.HashPassword(Password);

    private readonly IUserStore _store = Substitute.For<IUserStore>();
    private readonly IAuthEventStore _authEventStore = Substitute.For<IAuthEventStore>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly ITenantAuthConfigStore _configStore = Substitute.For<ITenantAuthConfigStore>();

    [Fact]
    public async Task ChangePassword_ShouldReturn400AndAnnounceNothing_WhenThePasswordWasReplacedMeanwhile()
    {
        ReadsReturn(mfaEnabled: false);

        var result = await AuthEndpoints.ChangePassword(
            new ChangePasswordRequest(Password, "Changed-Passw0rd!"), Context(), _store, _configStore,
            AuthEvents(), _notifications, CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>()
            .Which.Value!.Error.Should().Be("Current password is incorrect");
        await _store.Received(1).SetPasswordHashAsync(
            new TenantId(TenantId), EntityId.From(UserId), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
            false, s_passwordHash, Arg.Any<CancellationToken>());
        await _authEventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _notifications.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task ResetPassword_ShouldReturn400AndRecordNothing_WhenTheUserIsDeletedBeforeTheWrite()
    {
        ReadsReturn(mfaEnabled: false);
        var resetCache = new InMemoryPasswordResetCache();
        await resetCache.StoreAsync("reset-token", new PasswordResetEntry
        {
            UserId = UserId,
            TenantId = TenantId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        }, CancellationToken.None);

        var result = await AuthEndpoints.ResetPassword(
            new ResetPasswordRequest("reset-token", "Changed-Passw0rd!"), _store, _configStore,
            AuthEvents(), resetCache, CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>()
            .Which.Value!.Error.Should().Be("Invalid reset token");
        await _authEventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task MfaConfirm_ShouldReturn400AlreadyEnrolled_WhenAnotherRequestEnabledMfaMeanwhile()
    {
        // First read: enrollment pending. Re-read after the refused write: MFA is on.
        _store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(User(mfaEnabled: false, secret: Secret), User(mfaEnabled: true, secret: Secret));

        var result = await AuthEndpoints.MfaConfirm(
            new MfaConfirmRequest(TotpNow()), Context(), _store, AuthEvents(), _notifications, CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>()
            .Which.Value!.Error.Should().Be(AuthEndpoints.MfaAlreadyEnrolledMessage);
        await _authEventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _notifications.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task MfaConfirm_ShouldReturn400NotInitiated_WhenThePendingSecretWasClearedMeanwhile()
    {
        _store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(User(mfaEnabled: false, secret: Secret), User(mfaEnabled: false, secret: null));

        var result = await AuthEndpoints.MfaConfirm(
            new MfaConfirmRequest(TotpNow()), Context(), _store, AuthEvents(), _notifications, CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>()
            .Which.Value!.Error.Should().Be("MFA setup not initiated");
    }

    [Fact]
    public async Task MfaDisable_ShouldReturn401AndAnnounceNothing_WhenTheUserIsDeletedBeforeTheWrite()
    {
        ReadsReturn(mfaEnabled: true);

        var result = await AuthEndpoints.MfaDisable(
            new MfaDisableRequest(Password), Context(), _store, _configStore, AuthEvents(), _notifications, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedHttpResult>();
        await _authEventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        await _notifications.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_ShouldReturn400AndRecordNothing_WhenMfaWasDisabledMeanwhile()
    {
        ReadsReturn(mfaEnabled: true);

        var result = await AuthEndpoints.RegenerateRecoveryCodes(
            new RegenerateRecoveryCodesRequest(Password), Context(), _store, AuthEvents(), CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>()
            .Which.Value!.Error.Should().Be("MFA is not enabled for this user.");
        await _authEventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task ProfileRegenerate_ShouldReturn400AndAuditNothing_WhenMfaWasDisabledMeanwhile()
    {
        ReadsReturn(mfaEnabled: true);
        var audit = Substitute.For<IAuditService>();

        var result = await ProfileRecoveryCodesEndpoints.Regenerate(
            new ProfileRegenerateRecoveryCodesRequest(TotpNow()), Context(), _store, new RecoveryCodeService(),
            AuthEvents(), audit, CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>()
            .Which.Value!.Error.Should().Be("MFA is not enabled for this user.");
        audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task EnrollVerify_ShouldReturn400AndRecordNothing_WhenMfaWasEnabledMeanwhile()
    {
        ReadsReturn(mfaEnabled: false);

        var result = await MfaEnrollEndpoints.Verify(
            new MfaEnrollVerifyRequest(Secret, TotpNow()), Context(), _store, new RecoveryCodeService(),
            AuthEvents(), CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>()
            .Which.Value!.Error.Should().Be("MFA already enrolled.");
        await _authEventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task EnrollComplete_ShouldReturn204WithoutASecondEvent_WhenAConcurrentReplayCompletedFirst()
    {
        _store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(User(mfaEnabled: false, secret: Secret), User(mfaEnabled: true, secret: Secret));

        var result = await MfaEnrollEndpoints.Complete(
            new MfaEnrollCompleteRequest(Acknowledged: true), Context(), _store, AuthEvents(), CancellationToken.None);

        result.Should().BeOfType<NoContent>(because: "completion is idempotent");
        await _authEventStore.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task EnrollComplete_ShouldReturn400_WhenThePendingSecretWasClearedMeanwhile()
    {
        _store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(User(mfaEnabled: false, secret: Secret), User(mfaEnabled: false, secret: null));

        var result = await MfaEnrollEndpoints.Complete(
            new MfaEnrollCompleteRequest(Acknowledged: true), Context(), _store, AuthEvents(), CancellationToken.None);

        result.Should().BeOfType<BadRequest<ErrorResponse>>();
    }

    private void ReadsReturn(bool mfaEnabled) =>
        _store.GetByIdAsync(Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Any<CancellationToken>())
            .Returns(_ => User(mfaEnabled, mfaEnabled ? Secret : null));

    private AuthEventService AuthEvents() => new(_authEventStore);

    private static User User(bool mfaEnabled, string? secret) => new()
    {
        UserId = EntityId.From(UserId),
        TenantId = new TenantId(TenantId),
        Email = "refusal@test.example",
        DisplayName = "Refusal",
        Role = UserRole.Agent,
        Status = UserStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        PasswordHash = s_passwordHash,
        MfaEnabled = mfaEnabled,
        MfaSecret = secret,
        MfaRecoveryCodes = mfaEnabled ? ["digest"] : null,
        MfaConfirmedAt = mfaEnabled ? DateTimeOffset.UtcNow.AddDays(-1) : null,
    };

    private static DefaultHttpContext Context() => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tid", TenantId), new Claim("sub", UserId)], "test")),
        RequestServices = new ServiceCollection().BuildServiceProvider(),
    };

    private static string TotpNow() => new Totp(Base32Encoding.ToBytes(Secret)).ComputeTotp(DateTime.UtcNow);
}
