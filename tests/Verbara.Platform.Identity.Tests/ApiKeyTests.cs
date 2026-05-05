using Verbara.Platform.Core;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Identity.Tests;

public class ApiKeyTests
{
    [Fact]
    public void IsExpired_ShouldReturnFalse_WhenNoExpiration()
    {
        var key = new ApiKey
        {
            KeyId = EntityId.From("k-001"),
            TenantId = new TenantId("t1"),
            Name = "Test Key",
            HashedKey = "hashed",
            Scopes = ["conversations:read"],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        key.IsExpired(DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_ShouldReturnTrue_WhenPastExpiration()
    {
        var key = new ApiKey
        {
            KeyId = EntityId.From("k-001"),
            TenantId = new TenantId("t1"),
            Name = "Test Key",
            HashedKey = "hashed",
            Scopes = ["conversations:read"],
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        key.IsExpired(DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void HasScope_ShouldReturnTrue_WhenWildcard()
    {
        var key = new ApiKey
        {
            KeyId = EntityId.From("k-001"),
            TenantId = new TenantId("t1"),
            Name = "Admin Key",
            HashedKey = "hashed",
            Scopes = ["admin:*"],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        key.HasScope("admin:users").Should().BeTrue();
    }

    [Fact]
    public void HasScope_ShouldReturnTrue_WhenExactMatch()
    {
        var key = new ApiKey
        {
            KeyId = EntityId.From("k-001"),
            TenantId = new TenantId("t1"),
            Name = "Read Key",
            HashedKey = "hashed",
            Scopes = ["conversations:read", "queues:read"],
            CreatedAt = DateTimeOffset.UtcNow,
        };

        key.HasScope("conversations:read").Should().BeTrue();
        key.HasScope("conversations:write").Should().BeFalse();
    }
}
