using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Ami.Responses;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// call-records-are-written-once design D5 (task B.3b): the event store's tenant resolver by LinkedId — the Conversation
/// first, then the live call's trunk leg, else no tenant. The end-to-end record path is
/// <c>EventStore/CallTenantResolvedAtAcquisitionTests</c>.
/// </summary>
public sealed class VoiceCallTenantResolverTests : IAsyncDisposable
{
    private const string LinkedId = "1762430400.401";
    private const string Channel = "PJSIP/t-9-00000401";

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly ICallSessionManager _sessions = Substitute.For<ICallSessionManager>();
    private readonly IAmiConnection _ami = Substitute.For<IAmiConnection>();
    private readonly VerbaraServerPool _serverPool =
        new(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);

    public ValueTask DisposeAsync() => _serverPool.DisposeAsync();

    private VoiceCallTenantResolver CreateResolver() =>
        new(_conversations, _sessions, _serverPool, NullLogger<VoiceCallTenantResolver>.Instance);

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldAnswerTheConversationTenant_WhenAConversationHasTheLinkedId()
    {
        _conversations.FindByVoiceLinkedIdAcrossTenantsAsync(LinkedId, Arg.Any<CancellationToken>())
            .Returns(new Conversation
            {
                ConversationId = EntityId.New(),
                TenantId = new TenantId("acme"),
                ContactId = EntityId.New(),
                Channel = ChannelType.Voice,
                State = ConversationState.WrapUp,
                CreatedAt = DateTimeOffset.UtcNow,
                VoiceLinkedId = LinkedId,
            });

        var tenant = await CreateResolver().ResolveTenantIdAsync(LinkedId);

        tenant.Should().Be("acme");
        _sessions.DidNotReceive().GetByLinkedId(Arg.Any<string>());
    }

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldReadTheTrunkLeg_WhenNoConversationAndTheCallIsLive()
    {
        var session = LiveSession();
        StubServer("Success", "beta");

        var tenant = await CreateResolver().ResolveTenantIdAsync(LinkedId);

        tenant.Should().Be("beta");
        session.TenantId.Should().Be("beta", "the read stamps the session, as the bridge's does");
    }

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldAnswerNoTenant_WhenTheTrunkLegHasNoTenantId()
    {
        LiveSession();
        StubServer("Success", "");

        var tenant = await CreateResolver().ResolveTenantIdAsync(LinkedId);

        tenant.Should().BeNull();
    }

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldAnswerNoTenant_WhenTheCallEndedOrIsUnknown()
    {
        var ended = LiveSession();
        typeof(CallSession).GetProperty(nameof(CallSession.State))!.SetValue(ended, CallSessionState.Completed);
        StubServer("Success", "beta");

        var endedTenant = await CreateResolver().ResolveTenantIdAsync(LinkedId);
        var unknownTenant = await CreateResolver().ResolveTenantIdAsync("1762430400.999");

        endedTenant.Should().BeNull("an ended call's channels are gone, so nothing is read");
        unknownTenant.Should().BeNull();
#pragma warning disable CA2012 // ValueTask in NSubstitute verification
        await _ami.DidNotReceiveWithAnyArgs().SendActionAsync<GetVarResponse>(default!, default);
#pragma warning restore CA2012
    }

    [Fact]
    public async Task ResolveTenantIdAsync_ShouldThrow_WhenTheConversationStoreFails()
    {
        _conversations.FindByVoiceLinkedIdAcrossTenantsAsync(LinkedId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("store down"));

        var resolve = async () => await CreateResolver().ResolveTenantIdAsync(LinkedId);

        await resolve.Should().ThrowAsync<TimeoutException>("the event store retries a resolver that throws");
    }

    private CallSession LiveSession()
    {
        var session = new CallSession("s-401", LinkedId, "primary", CallDirection.Inbound);
        var participant = new SessionParticipant
        {
            UniqueId = Channel,
            Channel = Channel,
            Technology = "PJSIP",
            Role = ParticipantRole.Caller,
        };
        var add = typeof(CallSession).GetMethod("AddParticipant", BindingFlags.Instance | BindingFlags.NonPublic)
                  ?? throw new InvalidOperationException("CallSession.AddParticipant not found.");
        add.Invoke(session, [participant]);
        _sessions.GetByLinkedId(LinkedId).Returns(session);
        return session;
    }

    private void StubServer(string response, string value)
    {
#pragma warning disable CA2012 // ValueTask in NSubstitute setup
        _ami.SendActionAsync<GetVarResponse>(Arg.Any<ManagerAction>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<GetVarResponse>(new GetVarResponse { Response = response, Value = value }));
#pragma warning restore CA2012
        _serverPool.AddExistingServer("primary", new VerbaraServer(_ami, NullLogger<VerbaraServer>.Instance));
    }
}
