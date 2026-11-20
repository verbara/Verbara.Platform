using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The password sign-in verifies inside the replica's <see cref="PasswordHashingGate"/> (openspec
/// <c>bound-concurrent-password-hashing</c>): a sign-in that finds every hashing slot taken waits for one and
/// then succeeds, and a burst larger than the bound signs everyone in. Before the gate, a burst ran one hash
/// per request on the thread pool at once and starved the rest of the replica.
/// </summary>
public sealed class LoginHashingBoundTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Login_ShouldWaitForAHashingSlot_WhenTheGateIsFull()
    {
        var fixture = NewFixture();
        using var gate = new PasswordHashingGate(1);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() => gate.RunAsync(() => release.Wait(Deadline), CancellationToken.None));
        await WaitUntilAsync(() => gate.AvailableSlots == 0);

        var login = InvokeLoginAsync(fixture, gate);
        await Task.Delay(Settle); // fence-allow: SETTLE — a negative check: give an ungated sign-in time to finish before asserting it has not

        login.IsCompleted.Should().BeFalse("the password check needs a hashing slot and the only one is held");

        release.Set();
        (await login.WaitAsync(Deadline)).Should().BeOfType<Ok<TokenResponse>>();
        await holder.WaitAsync(Deadline);
        gate.AvailableSlots.Should().Be(1);
    }

    [Fact]
    public async Task Login_ShouldSignEveryoneIn_WhenABurstExceedsTheBound()
    {
        var fixture = NewFixture();
        using var gate = new PasswordHashingGate(2);

        var burst = Enumerable.Range(0, 16).Select(_ => Task.Run(() => InvokeLoginAsync(fixture, gate))).ToArray();
        var results = await Task.WhenAll(burst).WaitAsync(Deadline);

        results.Should().AllBeOfType<Ok<TokenResponse>>("callers over the bound wait for a slot; none is refused");
        gate.AvailableSlots.Should().Be(2);
    }

    [Fact]
    public async Task Login_ShouldStillRefuseAWrongPassword_WhenVerifiedThroughTheGate()
    {
        var fixture = NewFixture();
        using var gate = new PasswordHashingGate(1);

        var result = await InvokeLoginAsync(fixture, gate, "not-the-password");

        result.Should().BeOfType<UnauthorizedHttpResult>();
        gate.AvailableSlots.Should().Be(1);
    }

    private static AuthHandlerFixture NewFixture() =>
        new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Agent)
            .WithTenantPolicy(new TenantAuthConfig { TenantId = AuthHandlerFixture.TenantId, MfaPolicy = "optional" });

    private static Task<IResult> InvokeLoginAsync(
        AuthHandlerFixture fixture, PasswordHashingGate gate, string password = AuthHandlerFixture.Password)
    {
        var context = AuthHandlerFixture.BuildHttpContext();
        context.RequestServices = new ServiceCollection().AddSingleton(gate).BuildServiceProvider();
        return AuthEndpoints.Login(
            new LoginRequest(AuthHandlerFixture.TenantId, AuthHandlerFixture.Email, password),
            context,
            fixture.UserStore,
            fixture.LockoutService,
            fixture.JwtService,
            fixture.RefreshService,
            fixture.AuthEvents,
            fixture.ConfigStore,
            fixture.MfaPolicyEvaluator,
            fixture.MfaPendingCache,
            authWriteQueue: null,
            fixture.TenantStore,
            CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Deadline);
        while (!condition())
            await Task.Delay(10, cts.Token); // fence-allow: LOOP-DRIVER — bounded poll on the gate's slot count, which raises no signal of its own
    }
}
