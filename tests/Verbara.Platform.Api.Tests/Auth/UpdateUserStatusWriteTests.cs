using System.Security.Claims;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// PUT /admin/users/{id} when the account changes between the handler's read and its write. The
/// changed fields go to the store in one write, which reports the values it replaced — and that
/// answer, not the snapshot the request read, decides whether access is revoked and what the audit
/// entry says.
/// </summary>
/// <remarks>
/// The handler is invoked directly (InternalsVisibleTo) with a substituted store, the only way to
/// put a concurrent change between its read and its write deterministically.
/// </remarks>
public sealed class UpdateUserStatusWriteTests : IDisposable
{
    private const string Tenant = "t-update-user";
    private const string TargetId = "u-target";
    private const string AdminId = "u-admin";

    private readonly IUserStore _store = Substitute.For<IUserStore>();
    private readonly IRefreshTokenStore _refreshTokens = Substitute.For<IRefreshTokenStore>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly PlatformEventBus _eventBus = new();
    private readonly List<PlatformEvent> _published = [];
    private readonly IDisposable _subscription;
    private readonly IClock _clock = Substitute.For<IClock>();

    public UpdateUserStatusWriteTests()
    {
        // Every read returns a fresh Active snapshot, as PostgresUserStore does.
        _store.GetByIdAsync(new TenantId(Tenant), EntityId.From(TargetId), Arg.Any<CancellationToken>())
            .Returns(_ => NewTarget());
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        _subscription = _eventBus.Events.Subscribe(_published.Add);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _eventBus.Dispose();
    }

    [Fact]
    public async Task UpdateUser_ShouldReturn404WithoutRevoking_WhenTheUserIsDeletedBeforeTheWrite()
    {
        WriteReturns(AdminFieldsWriteResult.NotFound);

        var result = await InvokeAsync(new UpdateUserRequest(DisplayName: null, Role: null, Status: UserStatus.Suspended));

        result.Result.Should().BeOfType<NotFound>();
        await _refreshTokens.DidNotReceiveWithAnyArgs().RevokeAllForUserAsync(default!, default!, default, default);
        _audit.ReceivedCalls().Should().BeEmpty(because: "no status changed, so there is nothing to audit");
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateUser_ShouldReturn404_WhenTheUserIsDeletedBeforeTheRoleWrite()
    {
        WriteReturns(AdminFieldsWriteResult.NotFound);

        var result = await InvokeAsync(new UpdateUserRequest(DisplayName: null, Role: UserRole.Admin, Status: null));

        result.Result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task UpdateUser_ShouldWriteOnlyTheChangedFieldsInOneWrite_WhenSeveralFieldsAreSent()
    {
        WriteReturns(AdminFieldsWriteResult.Written(
            new AdminFields("Target", UserRole.Agent, UserStatus.Active),
            NewTarget(displayName: "Renamed", role: UserRole.Supervisor)));

        // Platform.Web sends every field on each edit; the status here is the one already stored.
        await InvokeAsync(new UpdateUserRequest(DisplayName: "Renamed", Role: UserRole.Supervisor, Status: UserStatus.Active));

        await _store.Received(1).UpdateAdminFieldsAsync(
            new TenantId(Tenant), EntityId.From(TargetId),
            Arg.Is<AdminFieldsChange>(c => c.DisplayName == "Renamed" && c.Role == UserRole.Supervisor
                && c.Status == null && c.Expected == null),
            Arg.Any<DateTimeOffset>(), AdminId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUser_ShouldNotWrite_WhenNothingDiffersFromWhatWasRead()
    {
        var result = await InvokeAsync(new UpdateUserRequest(DisplayName: "Target", Role: UserRole.Agent, Status: UserStatus.Active));

        result.Result.Should().BeOfType<Ok<UserDto>>();
        await _store.DidNotReceiveWithAnyArgs().UpdateAdminFieldsAsync(default, default, default!, default, default, default);
    }

    [Fact]
    public async Task UpdateUser_ShouldReturnTheUserAsStored_WhenAnotherWriteLandedFirst()
    {
        // Another admin renamed the user between this request's read and its role write.
        WriteReturns(AdminFieldsWriteResult.Written(
            new AdminFields("Renamed elsewhere", UserRole.Agent, UserStatus.Active),
            NewTarget(displayName: "Renamed elsewhere", role: UserRole.Supervisor)));

        var result = await InvokeAsync(new UpdateUserRequest(DisplayName: null, Role: UserRole.Supervisor, Status: null));

        var dto = result.Result.Should().BeOfType<Ok<UserDto>>().Which.Value!;
        dto.DisplayName.Should().Be("Renamed elsewhere");
        dto.Role.Should().Be("supervisor");
    }

    [Fact]
    public async Task UpdateUser_ShouldAuditTheStatusTheStoreReplaced_WhenAnotherAdminChangedItFirst()
    {
        // This request read Active; another admin suspended the account before this deactivation
        // was written, so the transition that actually happened is Suspended -> Deactivated.
        WriteReturns(AdminFieldsWriteResult.Written(
            new AdminFields("Target", UserRole.Agent, UserStatus.Suspended),
            NewTarget(status: UserStatus.Deactivated)));

        var result = await InvokeAsync(new UpdateUserRequest(DisplayName: null, Role: null, Status: UserStatus.Deactivated));

        result.Result.Should().BeOfType<Ok<UserDto>>();
        await _audit.Received(1).RecordAsync(
            new TenantId(Tenant),
            "auth",
            "user.status_changed",
            "warning",
            AdminId,
            "user",
            TargetId,
            "User",
            Arg.Any<Guid?>(),
            Arg.Any<AuditChanges?>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(m =>
                m != null && m["old_status"] == "Suspended" && m["new_status"] == "Deactivated"),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUser_ShouldNotRevokeOrAuditAgain_WhenTheStoreAlreadyHeldTheRequestedStatus()
    {
        // Two admins suspend the account at once: the later write replaces Suspended with
        // Suspended, and the revocation and audit entry belong to the earlier one only.
        WriteReturns(AdminFieldsWriteResult.Written(
            new AdminFields("Target", UserRole.Agent, UserStatus.Suspended),
            NewTarget(status: UserStatus.Suspended)));

        var result = await InvokeAsync(new UpdateUserRequest(DisplayName: null, Role: null, Status: UserStatus.Suspended));

        result.Result.Should().BeOfType<Ok<UserDto>>()
            .Which.Value!.Status.Should().Be("suspended");
        await _refreshTokens.DidNotReceiveWithAnyArgs().RevokeAllForUserAsync(default!, default!, default, default);
        _audit.ReceivedCalls().Should().BeEmpty();
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateUser_ShouldReturn412AndChangeNothingElse_WhenTheUserChangesBetweenTheReadAndTheConditionalWrite()
    {
        // The form's tag names what this request read, but another admin changes the user before the
        // write, so the write that expects those values finds others.
        WriteReturns(AdminFieldsWriteResult.Stale);

        var result = await InvokeAsync(
            new UpdateUserRequest(DisplayName: null, Role: UserRole.Admin, Status: null),
            ifMatch: UserAdminFieldsTag.For(NewTarget()));

        result.Result.Should().BeOfType<ProblemHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        await _refreshTokens.DidNotReceiveWithAnyArgs().RevokeAllForUserAsync(default!, default!, default, default);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateUser_ShouldWriteOnlyWhileTheUserHoldsTheValuesRead_WhenIfMatchNamesThem()
    {
        WriteReturns(AdminFieldsWriteResult.Written(
            new AdminFields("Target", UserRole.Agent, UserStatus.Active), NewTarget(role: UserRole.Supervisor)));

        var result = await InvokeAsync(
            new UpdateUserRequest(DisplayName: null, Role: UserRole.Supervisor, Status: null),
            ifMatch: UserAdminFieldsTag.For(NewTarget()));

        result.Result.Should().BeOfType<Ok<UserDto>>();
        await _store.Received(1).UpdateAdminFieldsAsync(
            new TenantId(Tenant), EntityId.From(TargetId),
            Arg.Is<AdminFieldsChange>(c => c.Role == UserRole.Supervisor
                && c.Expected == new AdminFields("Target", UserRole.Agent, UserStatus.Active)),
            Arg.Any<DateTimeOffset>(), AdminId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUser_ShouldWriteWithoutExpectingValues_WhenIfMatchIsTheWildcard()
    {
        WriteReturns(AdminFieldsWriteResult.Written(
            new AdminFields("Target", UserRole.Agent, UserStatus.Active), NewTarget(role: UserRole.Supervisor)));

        await InvokeAsync(new UpdateUserRequest(DisplayName: null, Role: UserRole.Supervisor, Status: null), ifMatch: "*");

        await _store.Received(1).UpdateAdminFieldsAsync(
            Arg.Any<TenantId>(), Arg.Any<EntityId>(), Arg.Is<AdminFieldsChange>(c => c.Expected == null),
            Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private void WriteReturns(AdminFieldsWriteResult result) =>
        _store.UpdateAdminFieldsAsync(new TenantId(Tenant), EntityId.From(TargetId), Arg.Any<AdminFieldsChange>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private Task<Results<Ok<UserDto>, NotFound, ProblemHttpResult>> InvokeAsync(UpdateUserRequest body, string? ifMatch = null)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", AdminId)], "test")),
        };
        context.Items["TenantId"] = new TenantId(Tenant);
        if (ifMatch is not null)
            context.Request.Headers.IfMatch = ifMatch;
        var sessions = new SessionService(_refreshTokens, _store, new AuthEventService(Substitute.For<IAuthEventStore>()));

        return AdminEndpoints.UpdateUser(
            TargetId, context, body, _store, sessions, _audit, _eventBus, _clock, CancellationToken.None);
    }

    private static User NewTarget(
        string displayName = "Target", UserRole role = UserRole.Agent, UserStatus status = UserStatus.Active) => new()
    {
        UserId = EntityId.From(TargetId),
        TenantId = new TenantId(Tenant),
        Email = "target@update-user.test",
        DisplayName = displayName,
        Role = role,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
