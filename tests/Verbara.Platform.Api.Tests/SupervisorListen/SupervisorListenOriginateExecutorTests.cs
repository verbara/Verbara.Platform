using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 2.1 (design D2): the listen originates through a Platform-owned executor that never
/// consults a licence guard, sends on the primary AMI connection, and is registered under its own key — it never
/// replaces the Dialer executor click-to-dial resolves.
/// </summary>
public sealed class SupervisorListenOriginateExecutorTests : IAsyncDisposable
{
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly VerbaraServerPool _pool = new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    private static ILicenseGuard DenyEverything()
    {
        var guard = Substitute.For<ILicenseGuard>();
        guard.CanExecute(Arg.Any<LicenseFeature>()).Returns(new LicenseGuardResult(false, LicenseBlockReason.NotLicensed));
        guard.CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>())
            .Returns(new LicenseGuardResult(false, LicenseBlockReason.NotLicensed));
        return guard;
    }

    private void AddPrimary() =>
        _pool.AddExistingServer("primary", new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));

    private static OriginateAction Action() => new() { Channel = "PJSIP/acme-agent-sup-u1", Context = "supervisor-listen", Exten = "endpoint" };

    [Fact]
    public async Task ExecuteAsync_ShouldSendOnThePrimaryConnection_AndReportSuccess()
    {
        AddPrimary();
        _ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(new ManagerResponse { Response = "Success" });
        var action = Action();

        var result = await new SupervisorListenOriginateExecutor(_pool).ExecuteAsync(action, "primary", CancellationToken.None);

        result.Success.Should().BeTrue();
        await _ami.Received(1).SendActionAsync(action, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReportFailure_WhenAsteriskRefuses()
    {
        AddPrimary();
        _ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(new ManagerResponse { Response = "Error", Message = "Originate failed" });

        var result = await new SupervisorListenOriginateExecutor(_pool).ExecuteAsync(Action(), "primary", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Originate failed");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReportFailure_WhenTheSendThrows()
    {
        AddPrimary();
        _ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("socket closed"));

        var result = await new SupervisorListenOriginateExecutor(_pool).ExecuteAsync(Action(), "primary", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("socket closed");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReportFailure_WhenThereIsNoPrimaryServer()
    {
        var result = await new SupervisorListenOriginateExecutor(_pool).ExecuteAsync(Action(), "primary", CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task KeyedExecutor_ShouldNeverConsultTheLicenceGuard_AndNeverReplaceTheDialerExecutor()
    {
        AddPrimary();
        _ami.SendActionAsync(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(new ManagerResponse { Response = "Success" });
        var guard = DenyEverything();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_pool);
        services.AddSingleton(guard);
        // The Program.cs registration click-to-dial and the callback rescue resolve (TryAdd, licence-guarded).
        services.TryAddSingleton<OriginateExecutorBase>(sp => new DefaultOriginateExecutor(
            sp.GetRequiredService<VerbaraServerPool>(), sp.GetRequiredService<ILogger<DefaultOriginateExecutor>>(),
            licenseGuard: sp.GetService<ILicenseGuard>()));

        services.AddSupervisorListenOriginateExecutor();

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<OriginateExecutorBase>().Should().BeOfType<DefaultOriginateExecutor>(
            "the listen executor never replaces the Dialer executor");
        var listen = provider.GetRequiredKeyedService<OriginateExecutorBase>(SupervisorListenOriginateExecutor.ServiceKey);
        listen.Should().BeOfType<SupervisorListenOriginateExecutor>();

        var result = await listen.ExecuteAsync(Action(), "primary", CancellationToken.None);

        result.Success.Should().BeTrue("supervision consumes no licence (PDR-0003 §C.6)");
        guard.DidNotReceive().CanExecute(Arg.Any<LicenseFeature>());
        await guard.DidNotReceive().CanExecuteAsync(Arg.Any<LicenseFeature>(), Arg.Any<CancellationToken>());
        var dialer = await provider.GetRequiredService<OriginateExecutorBase>().ExecuteAsync(Action(), "primary", CancellationToken.None);
        dialer.SkipReason.Should().Be(OriginateSkipReason.LicenseBlocked, "the Dialer executor still enforces its licence");
    }
}
