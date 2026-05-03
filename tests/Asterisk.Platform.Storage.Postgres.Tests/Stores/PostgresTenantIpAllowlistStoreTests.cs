using Asterisk.Platform.Identity;
using Asterisk.Platform.Storage.Postgres.Stores;
using FluentAssertions;
using Xunit;

namespace Asterisk.Platform.Storage.Postgres.Tests.Stores;

public class PostgresTenantIpAllowlistStoreTests : IClassFixture<IpAllowlistFixture>, IAsyncLifetime
{
    private readonly IpAllowlistFixture _fixture;
    private readonly PostgresTenantIpAllowlistStore _sut;
    private readonly string _tenantId;

    public PostgresTenantIpAllowlistStoreTests(IpAllowlistFixture fixture)
    {
        _fixture = fixture;
        _sut = new PostgresTenantIpAllowlistStore(_fixture.DataSource);
        _tenantId = $"t-{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetAsync();
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO tenants (tenant_id) VALUES (@t) ON CONFLICT DO NOTHING";
        cmd.Parameters.AddWithValue("t", _tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddAsync_ShouldRoundtrip_WhenIpv4Cidr()
    {
        var added = await _sut.AddAsync(_tenantId, "192.0.2.0/24", "office", null, default);
        added.Cidr.Should().Be("192.0.2.0/24");

        var list = await _sut.ListAsync(_tenantId, default);
        list.Should().ContainSingle(e => e.Id == added.Id && e.Description == "office");
    }

    [Fact]
    public async Task AddAsync_ShouldRoundtrip_WhenIpv6Cidr()
    {
        var added = await _sut.AddAsync(_tenantId, "2001:db8::/32", "v6 vpn", null, default);
        added.Cidr.Should().Be("2001:db8::/32");
    }

    [Fact]
    public async Task AddAsync_ShouldReturnExisting_WhenDuplicateCidr()
    {
        var first = await _sut.AddAsync(_tenantId, "203.0.113.0/24", "first", null, default);
        var second = await _sut.AddAsync(_tenantId, "203.0.113.0/24", "second", null, default);
        second.Id.Should().Be(first.Id);
        second.Description.Should().Be("first");
    }

    [Fact]
    public async Task RemoveAsync_ShouldReturnTrue_WhenEntryExists()
    {
        var added = await _sut.AddAsync(_tenantId, "198.51.100.0/24", null, null, default);
        var removed = await _sut.RemoveAsync(_tenantId, added.Id, default);
        removed.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAsync_ShouldReturnFalse_WhenEntryMissing()
    {
        var removed = await _sut.RemoveAsync(_tenantId, Guid.NewGuid(), default);
        removed.Should().BeFalse();
    }

    [Fact]
    public async Task CountAsync_ShouldReturnEntryCount()
    {
        await _sut.AddAsync(_tenantId, "10.0.0.0/8", null, null, default);
        await _sut.AddAsync(_tenantId, "172.16.0.0/12", null, null, default);
        var count = await _sut.CountAsync(_tenantId, default);
        count.Should().Be(2);
    }
}
