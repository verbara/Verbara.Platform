using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.SupervisorListen;

/// <summary>
/// supervisor-silent-listen 2.2 (design D4): the uniqueid of the owner agent's live leg, picked from the call's
/// participants. The list is a parameter because <c>CallSession.AddParticipant</c> is SDK-internal.
/// </summary>
public sealed class AgentLegResolverTests
{
    private const string Tenant = "acme";
    private static readonly EntityId Owner = EntityId.From("0651f84b6f284a1c9b8d658330fa8310");
    private static readonly EntityId OtherAgent = EntityId.From("1a2b3c4d5e6f40718293a4b5c6d7e8f9");

    private static ConversationOwner AgentOwner(EntityId id) => new(ConversationOwnerKind.Agent, id);

    private static SessionParticipant Leg(string uniqueId, string channel, ParticipantRole role = ParticipantRole.Agent,
        DateTimeOffset? leftAt = null) => new()
    {
        UniqueId = uniqueId,
        Channel = channel,
        Technology = "PJSIP",
        Role = role,
        LeftAt = leftAt,
    };

    [Fact]
    public void Resolve_ShouldPickTheOwnersLiveLeg_WhenAQueueRangTwoMembers()
    {
        IReadOnlyList<SessionParticipant> legs =
        [
            Leg("1712345678.40", "PJSIP/acme-trunk-00000001", ParticipantRole.Caller),
            Leg("1712345678.41", $"PJSIP/acme-agent-{OtherAgent.Value}-00000002", leftAt: DateTimeOffset.UnixEpoch),
            Leg("1712345678.42", $"PJSIP/acme-agent-{Owner.Value}-00000003"),
        ];

        AgentLegResolver.Resolve(Tenant, AgentOwner(Owner), legs).Should().Be("1712345678.42");
    }

    [Fact]
    public void Resolve_ShouldPickTheALeg_OfAnOutboundClickToDial()
    {
        IReadOnlyList<SessionParticipant> legs =
        [
            Leg("1712345690.10", $"PJSIP/acme-agent-{Owner.Value}-0000000a", ParticipantRole.Caller),
            Leg("1712345690.11", "PJSIP/pstn-default-0000000b", ParticipantRole.Destination),
        ];

        AgentLegResolver.Resolve(Tenant, AgentOwner(Owner), legs).Should().Be("1712345690.10");
    }

    [Fact]
    public void Resolve_ShouldReturnNull_WhenTheConversationHasNoAgentOwner()
    {
        IReadOnlyList<SessionParticipant> legs = [Leg("1.1", $"PJSIP/acme-agent-{Owner.Value}-00000001")];

        AgentLegResolver.Resolve(Tenant, null, legs).Should().BeNull();
        AgentLegResolver.Resolve(Tenant, new ConversationOwner(ConversationOwnerKind.Queue, EntityId.New()), legs).Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldReturnNull_WhenTheOnlyMatchingLegIsAnotherTenants()
    {
        IReadOnlyList<SessionParticipant> legs = [Leg("1.1", $"PJSIP/globex-agent-{Owner.Value}-00000001")];

        AgentLegResolver.Resolve(Tenant, AgentOwner(Owner), legs).Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldReturnNull_WhenTheOwnersLegHasLeft()
    {
        IReadOnlyList<SessionParticipant> legs =
            [Leg("1.1", $"PJSIP/acme-agent-{Owner.Value}-00000001", leftAt: DateTimeOffset.UnixEpoch)];

        AgentLegResolver.Resolve(Tenant, AgentOwner(Owner), legs).Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldReturnNull_WhenThereIsNoSession()
    {
        AgentLegResolver.Resolve(Tenant, AgentOwner(Owner), null).Should().BeNull();
        AgentLegResolver.Resolve(Tenant, AgentOwner(Owner), []).Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldNotMatchAnotherAgentWhoseIdStartsWithTheOwners()
    {
        // The prefix ends with '-', so an id that merely starts with the owner's id never matches.
        IReadOnlyList<SessionParticipant> legs = [Leg("1.1", $"PJSIP/acme-agent-{Owner.Value}ff-00000001")];

        AgentLegResolver.Resolve(Tenant, AgentOwner(Owner), legs).Should().BeNull();
    }
}
