using System.Net;
using Verbara.Platform.Identity.Auth;
using Verbara.Platform.Realtime.Clients;
using Verbara.Platform.Realtime.Tests.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Realtime.Tests.Auth;

/// <summary>
/// The shipped Realtime host refuses, at connect, an impersonation token Platform.Api has revoked —
/// its session ended, revoked or timed out. The Api records the revocation in the jti revocation
/// store both hosts share (Redis, under the same key prefix as the JWT key pool Realtime already
/// needs to validate Api tokens); here the test writes it there directly.
/// </summary>
public sealed class ImpersonationRevocationHubTests : IClassFixture<RealtimeHostFixture>
{
    private const string TargetTenant = "acme";
    private const string ImpersonatorTenant = "platform";

    private readonly RealtimeHostFixture _host;

    public ImpersonationRevocationHubTests(RealtimeHostFixture host) => _host = host;

    [Fact]
    public async Task HubConnect_ShouldBeRefused_WhenImpersonationTokenRevoked()
    {
        _host.Status.Verdict = UserAccessVerdict.Allowed;
        var tokenId = Guid.NewGuid().ToString();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var token = _host.MintImpersonationToken(TargetTenant, NewImpersonatorId(), ImpersonatorTenant, tokenId, expiresAt);
        await _host.Services.GetRequiredService<IJtiRevocationCache>()
            .RevokeAsync(tokenId, expiresAt, CancellationToken.None);
        await using var connection = _host.ConnectOverWebSockets(() => token);

        var act = () => connection.StartAsync();

        (await act.Should().ThrowAsync<HttpRequestException>(
                because: "a revoked impersonation token must not open a hub connection for the rest of its life"))
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HubConnect_ShouldBeRefused_WhenImpersonationTokenCarriesNoTokenId()
    {
        // Every impersonation token Platform.Api mints carries a jti; one without cannot be revoked.
        _host.Status.Verdict = UserAccessVerdict.Allowed;
        var token = _host.MintImpersonationToken(
            TargetTenant, NewImpersonatorId(), ImpersonatorTenant, tokenId: null, DateTimeOffset.UtcNow.AddMinutes(30));
        await using var connection = _host.ConnectOverWebSockets(() => token);

        var act = () => connection.StartAsync();

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HubConnect_ShouldBeAdmitted_WhenImpersonationTokenNotRevoked()
    {
        _host.Status.Verdict = UserAccessVerdict.Allowed;
        var token = _host.MintImpersonationToken(
            TargetTenant, NewImpersonatorId(), ImpersonatorTenant, Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddMinutes(30));
        await using var connection = _host.ConnectOverWebSockets(() => token);

        await connection.StartAsync();

        connection.State.Should().Be(HubConnectionState.Connected);
    }

    private static string NewImpersonatorId() => $"impersonator-{Guid.NewGuid():N}";
}
