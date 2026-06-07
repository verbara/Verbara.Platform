using System.Threading;
using System.Threading.Tasks;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests;

public class TenantAuthConfigCapacityDefaultsProviderTests
{
    private static readonly TenantId Tenant = new("t1");

    private static (TenantAuthConfigCapacityDefaultsProvider Sut, ITenantAuthConfigStore Store) CreateSut(
        TenantAuthConfig? cfg)
    {
        var store = Substitute.For<ITenantAuthConfigStore>();
        store.GetAsync(Tenant.Value, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(cfg));

        return (new TenantAuthConfigCapacityDefaultsProvider(store), store);
    }

    [Fact]
    public async Task GetDefaultsAsync_ShouldFallBackToHardDefaults_WhenTenantConfigMissing()
    {
        var (sut, _) = CreateSut(cfg: null);

        var defaults = await sut.GetDefaultsAsync(Tenant, CancellationToken.None);

        defaults.MaxVoice.Should().Be(1);
        defaults.MaxChat.Should().Be(3);
        defaults.MaxEmail.Should().Be(5);
        defaults.MaxSms.Should().Be(3);
        defaults.MaxTotal.Should().Be(5);
    }

    [Fact]
    public async Task GetDefaultsAsync_ShouldMapTenantColumns_WhenConfigPresent()
    {
        var cfg = new TenantAuthConfig
        {
            TenantId = Tenant.Value,
            MaxVoiceDefault = 2,
            MaxChatDefault = 6,
            MaxEmailDefault = 8,
            MaxSmsDefault = 4,
            MaxTotalDefault = 12,
        };
        var (sut, _) = CreateSut(cfg);

        var defaults = await sut.GetDefaultsAsync(Tenant, CancellationToken.None);

        defaults.MaxVoice.Should().Be(2);
        defaults.MaxChat.Should().Be(6);
        defaults.MaxEmail.Should().Be(8);
        defaults.MaxSms.Should().Be(4);
        defaults.MaxTotal.Should().Be(12);
    }
}
