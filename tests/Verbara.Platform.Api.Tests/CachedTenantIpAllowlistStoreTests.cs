using Verbara.Platform.Api.Services;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Verbara.Platform.Api.Tests;

public sealed class CachedTenantIpAllowlistStoreTests : IDisposable
{
    private readonly InMemoryTenantIpAllowlistStore _inner = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly CachedTenantIpAllowlistStore _sut;

    public CachedTenantIpAllowlistStoreTests()
    {
        _sut = new CachedTenantIpAllowlistStore(_inner, _cache);
    }

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task ListAsync_ShouldHitCache_OnSecondCall()
    {
        await _inner.AddAsync("t1", "10.0.0.0/8", null, null, default);

        var first = await _sut.ListAsync("t1", default);

        // Mutate inner directly (bypassing the decorator) to prove the second
        // call returns cached data, not fresh.
        await _inner.AddAsync("t1", "172.16.0.0/12", null, null, default);

        var second = await _sut.ListAsync("t1", default);
        second.Should().HaveCount(first.Count);
    }

    [Fact]
    public async Task AddAsync_ShouldInvalidateCache()
    {
        await _inner.AddAsync("t1", "10.0.0.0/8", null, null, default);
        var warmup = await _sut.ListAsync("t1", default);
        warmup.Should().HaveCount(1);

        // Add through the decorator; its mutation path must invalidate.
        await _sut.AddAsync("t1", "172.16.0.0/12", null, null, default);

        var after = await _sut.ListAsync("t1", default);
        after.Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveAsync_ShouldInvalidateCache()
    {
        var entry = await _sut.AddAsync("t1", "10.0.0.0/8", null, null, default);
        var warmup = await _sut.ListAsync("t1", default);
        warmup.Should().HaveCount(1);

        await _sut.RemoveAsync("t1", entry.Id, default);

        var after = await _sut.ListAsync("t1", default);
        after.Should().BeEmpty();
    }
}
