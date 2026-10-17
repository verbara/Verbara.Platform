using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// #385 — <see cref="RegistrationReloadingTrunkStore"/> raises the in-memory reload signal after a
/// successful write that adds, changes or removes a trunk registration, and never otherwise.
/// </summary>
public sealed class RegistrationReloadingTrunkStoreTests
{
    private const string Tenant = "acme";

    private readonly TrunkStoreBase _inner = Substitute.For<TrunkStoreBase>();
    private readonly TrunkRegistrationReloadSignal _signal = new();
    private readonly RegistrationReloadingTrunkStore _sut;

    public RegistrationReloadingTrunkStoreTests()
    {
        _sut = new RegistrationReloadingTrunkStore(_inner, _signal);
    }

    private static Trunk WithRegistration(long id = 1) => new()
    {
        Id = id, Name = "carrier", RegistrationUri = "sip:sip.carrier.example",
        ClientUri = "sip:acct100@sip.carrier.example",
    };

    private static Trunk WithoutRegistration(long id = 1) => new() { Id = id, Name = "ip-trunk", MatchHost = "203.0.113.10" };

    [Fact]
    public async Task CreateAsync_ShouldSignal_WhenTrunkHasRegistrationUri()
    {
        _inner.CreateAsync(Arg.Any<Trunk>(), Tenant, Arg.Any<CancellationToken>()).Returns(7L);

        var id = await _sut.CreateAsync(WithRegistration(), Tenant);

        id.Should().Be(7);
        _signal.IsDirty.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CreateAsync_ShouldNotSignal_WhenTrunkHasNoRegistrationUri(string? registrationUri)
    {
        var trunk = WithoutRegistration();
        trunk.RegistrationUri = registrationUri;

        await _sut.CreateAsync(trunk, Tenant);

        _signal.IsDirty.Should().BeFalse();
        await _inner.Received(1).CreateAsync(trunk, Tenant, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_ShouldNotSignalAndShouldPropagate_WhenInnerThrows()
    {
        _inner.CreateAsync(Arg.Any<Trunk>(), Tenant, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = async () => await _sut.CreateAsync(WithRegistration(), Tenant);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("db down");
        _signal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_ShouldSignal_WhenUpdateRemovesTheRegistrationUri()
    {
        _inner.GetAsync(1, Tenant, Arg.Any<CancellationToken>()).Returns(WithRegistration());

        await _sut.UpdateAsync(WithoutRegistration(), Tenant);

        _signal.IsDirty.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_ShouldSignal_WhenUpdateAddsARegistrationUri()
    {
        _inner.GetAsync(1, Tenant, Arg.Any<CancellationToken>()).Returns(WithoutRegistration());

        await _sut.UpdateAsync(WithRegistration(), Tenant);

        _signal.IsDirty.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_ShouldNotSignal_WhenNeitherBeforeNorAfterHasARegistration()
    {
        _inner.GetAsync(1, Tenant, Arg.Any<CancellationToken>()).Returns(WithoutRegistration());

        await _sut.UpdateAsync(WithoutRegistration(), Tenant);

        _signal.IsDirty.Should().BeFalse();
        await _inner.Received(1).UpdateAsync(Arg.Any<Trunk>(), Tenant, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_ShouldNotSignalAndShouldPropagate_WhenInnerThrows()
    {
        _inner.GetAsync(1, Tenant, Arg.Any<CancellationToken>()).Returns(WithRegistration());
        _inner.UpdateAsync(Arg.Any<Trunk>(), Tenant, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = async () => await _sut.UpdateAsync(WithRegistration(), Tenant);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _signal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ShouldSignal_WhenDeletedTrunkHadARegistrationUri()
    {
        _inner.GetAsync(1, Tenant, Arg.Any<CancellationToken>()).Returns(WithRegistration());

        await _sut.DeleteAsync(1, Tenant);

        _signal.IsDirty.Should().BeTrue();
        await _inner.Received(1).DeleteAsync(1, Tenant, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_ShouldNotSignal_WhenDeletedTrunkHadNoRegistration()
    {
        _inner.GetAsync(1, Tenant, Arg.Any<CancellationToken>()).Returns(WithoutRegistration());

        await _sut.DeleteAsync(1, Tenant);

        _signal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ShouldNotSignal_WhenTrunkDoesNotExist()
    {
        _inner.GetAsync(1, Tenant, Arg.Any<CancellationToken>()).Returns(default(Trunk));

        await _sut.DeleteAsync(1, Tenant);

        _signal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ShouldNotSignalAndShouldPropagate_WhenInnerThrows()
    {
        _inner.GetAsync(1, Tenant, Arg.Any<CancellationToken>()).Returns(WithRegistration());
        _inner.DeleteAsync(1, Tenant, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = async () => await _sut.DeleteAsync(1, Tenant);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _signal.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Reads_ShouldPassThroughToInner()
    {
        var trunk = WithRegistration();
        IReadOnlyList<Trunk> all = [trunk];
        _inner.GetAsync(1, Tenant, Arg.Any<CancellationToken>()).Returns(trunk);
        _inner.GetByNameAsync("carrier", Tenant, Arg.Any<CancellationToken>()).Returns(trunk);
        _inner.ListAsync(Tenant, Arg.Any<CancellationToken>()).Returns(all);
        _inner.ListActiveAsync(Tenant, Arg.Any<CancellationToken>()).Returns(all);

        (await _sut.GetAsync(1, Tenant)).Should().BeSameAs(trunk);
        (await _sut.GetByNameAsync("carrier", Tenant)).Should().BeSameAs(trunk);
        (await _sut.ListAsync(Tenant)).Should().BeSameAs(all);
        (await _sut.ListActiveAsync(Tenant)).Should().BeSameAs(all);
        _signal.IsDirty.Should().BeFalse();
    }
}
