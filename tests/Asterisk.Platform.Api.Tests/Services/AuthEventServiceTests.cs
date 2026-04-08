using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Identity;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Services;

public sealed class AuthEventServiceTests
{
    private readonly IAuthEventStore _store = Substitute.For<IAuthEventStore>();
    private readonly AuthEventService _sut;

    public AuthEventServiceTests()
    {
        _sut = new AuthEventService(_store);
    }

    [Fact]
    public async Task LogAsync_ShouldPersistEvent()
    {
        await _sut.LogAsync("t1", "u1", AuthEventTypes.LoginSuccess, "127.0.0.1", "TestAgent", null, CancellationToken.None);

        await _store.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e =>
                e.TenantId == "t1" &&
                e.UserId == "u1" &&
                e.EventType == AuthEventTypes.LoginSuccess &&
                e.IpAddress == "127.0.0.1" &&
                e.UserAgent == "TestAgent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogAsync_ShouldIncludeDetails_WhenProvided()
    {
        var details = new Dictionary<string, string> { ["reason"] = "bad password" };

        await _sut.LogAsync("t1", "u1", AuthEventTypes.LoginFailure, null, null, details, CancellationToken.None);

        await _store.Received(1).SaveAsync(
            Arg.Is<AuthEvent>(e =>
                e.Details != null &&
                e.Details.RootElement.GetProperty("reason").GetString() == "bad password"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListByTenantAsync_ShouldDelegateToStore()
    {
        await _sut.ListByTenantAsync("t1", 1, 25, CancellationToken.None);

        await _store.Received(1).ListByTenantAsync("t1", 1, 25, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListByUserAsync_ShouldDelegateToStore()
    {
        await _sut.ListByUserAsync("t1", "u1", 1, 25, CancellationToken.None);

        await _store.Received(1).ListByUserAsync("t1", "u1", 1, 25, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_ShouldDelegateToStore()
    {
        var query = new AuthEventQuery(EventType: "login_success");
        await _sut.SearchAsync("t1", query, CancellationToken.None);
        await _store.Received(1).SearchAsync("t1", query, Arg.Any<CancellationToken>());
    }
}
