using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Core.Configuration;
using Verbara.Sdk.Pro.AgentAssist.Features;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Verbara.Platform.Api.Tests;

public sealed class InMemoryAgentAssistFeatureToggleTests
{
    private static AgentAssistCredentialsProtector CreateProtector() =>
        new(new EphemeralDataProtectionProvider());

    private static InMemoryAgentAssistFeatureToggle Create(AgentAssistOptions options, AgentAssistCredentialsProtector? protector = null) =>
        new(Options.Create(options), protector ?? CreateProtector());

    [Fact]
    public async Task GetCurrentAsync_ShouldSeedFromOptions_WhenFirstRead()
    {
        var options = new AgentAssistOptions
        {
            Enabled = true,
            Provider = "deepgram",
            Deepgram = new AgentAssistProviderCredentials { ApiKey = "dg-seed-key" },
        };
        var toggle = Create(options);

        var state = await toggle.GetCurrentAsync();

        state.Enabled.Should().BeTrue();
        state.Provider.Should().Be("deepgram");
        state.ProtectedCredentials.Should().NotBeNull();
        state.ProtectedCredentials!.Should().ContainKey("apiKey");
        state.ProtectedCredentials!["apiKey"].Should().NotBe("dg-seed-key",
            because: "seed credentials are always encrypted before reaching the live state");
        state.UpdatedBy.Should().Be("appsettings");
    }

    [Fact]
    public async Task SetAsync_ShouldReplaceState_AndPersistAcrossReads()
    {
        var toggle = Create(new AgentAssistOptions { Enabled = false });
        var protector = CreateProtector();
        var encrypted = protector.Protect(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["apiKey"] = "new-key",
        });

        var desired = new AgentAssistFeatureState(
            Enabled: true, Provider: "whisper",
            ProtectedCredentials: encrypted,
            UpdatedAt: DateTimeOffset.UtcNow,
            UpdatedBy: "test-user");

        var persisted = await toggle.SetAsync(desired);
        persisted.Should().BeEquivalentTo(desired);

        var subsequent = await toggle.GetCurrentAsync();
        subsequent.Should().BeEquivalentTo(desired);
    }

    [Fact]
    public async Task GetCurrentAsync_ShouldBeConcurrencySafe_WhenManyReaders()
    {
        // 200 concurrent readers across a single instance — must never throw and must always
        // return a state that equals the last Set() value (or the seed, if never Set).
        var toggle = Create(new AgentAssistOptions
        {
            Enabled = true,
            Provider = "google",
            Google = new AgentAssistProviderCredentials { ApiKey = "{\"type\":\"service_account\"}" },
        });

        var readTasks = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => toggle.GetCurrentAsync().AsTask()))
            .ToArray();

        var results = await Task.WhenAll(readTasks);

        results.Should().AllSatisfy(r =>
        {
            r.Enabled.Should().BeTrue();
            r.Provider.Should().Be("google");
        });
    }
}
