using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-sessions-stay-in-their-tenant task 1.1 (design D2, audit missed P2): the production adapter's mapping,
/// over real Sdk <see cref="CallSession"/> values. The projection line of <see cref="AgentAssistLiveSessions.Snapshot"/>
/// over a session the Pro engine registered is covered by <see cref="AgentAssistLiveSessionsRegisteredSessionTests"/>.
/// </summary>
public sealed class AgentAssistLiveSessionsAdapterTests
{
    private static CallSession Call(string linkedId, string? tenant = null)
    {
        var call = new CallSession($"s-{linkedId}", linkedId, "primary", CallDirection.Inbound);
        if (tenant is not null)
            call.TenantId = tenant;
        return call;
    }

    [Fact]
    public void ToLive_ShouldOwnTheCallTenant_WhenTheCallIsStampedAfterConstructionAndTheSnapshotIsEmpty()
    {
        var call = Call("1712345678.10");
        call.TenantId = "acme";   // stamped after the session took its (empty) snapshot

        var live = AgentAssistLiveSessions.ToLive("aa-1", call, sessionTenant: "");

        live.Should().Be(new LiveAssistSession("aa-1", "acme", call));
    }

    [Fact]
    public void ToLive_ShouldReadTheCallTenantWhenCalled_WhenTheStampChangesBetweenCalls()
    {
        var call = Call("1712345678.11");

        var before = AgentAssistLiveSessions.ToLive("aa-2", call, sessionTenant: string.Empty);
        call.TenantId = "acme";
        var after = AgentAssistLiveSessions.ToLive("aa-2", call, sessionTenant: string.Empty);

        before.OwnerTenant.Should().BeNull("nothing is stamped yet");
        after.OwnerTenant.Should().Be("acme", "the call's tenant is read when the session is mapped, not cached");
    }

    [Fact]
    public void ToLive_ShouldOwnNothing_WhenCallAndSnapshotDiffer()
    {
        var call = Call("1712345678.12", tenant: "acme");

        AgentAssistLiveSessions.ToLive("aa-3", call, sessionTenant: "globex").OwnerTenant.Should().BeNull();
    }

    [Fact]
    public void ToLive_ShouldOwnTheSnapshot_WhenTheCallIsNull()
    {
        var live = AgentAssistLiveSessions.ToLive("aa-4", call: null, sessionTenant: "acme");

        live.Should().Be(new LiveAssistSession("aa-4", "acme", null));
    }

    [Fact]
    public void Snapshot_ShouldBeEmpty_WhenNoAgentAssistSupervisorIsRegistered()
    {
        var adapter = new AgentAssistLiveSessions(supervisor: null);

        adapter.Snapshot().Should().BeEmpty();
        adapter.TryWhisper("aa-5", "hello").Should().BeFalse();
    }

    [Fact]
    public void Snapshot_ShouldBeEmpty_WhenTheSupervisorHasNoSessions()
    {
        using var supervisor = new AgentAssistSupervisor();

        new AgentAssistLiveSessions(supervisor).Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void TryWhisper_ShouldReturnFalse_WhenTheIdNamesNoSession()
    {
        using var supervisor = new AgentAssistSupervisor();

        new AgentAssistLiveSessions(supervisor).TryWhisper("aa-unknown", "hello").Should().BeFalse();
    }
}
