using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Tests.Sse;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// contact-history-opens-by-assignment task 3.2 (spec <c>conversation-read-scope</c> "The read rule MUST be one shared
/// rule"; spec audit F5): the route-parity check. Each caller of the task 2.1 matrix reads each conversation case through
/// the four governed routes of the real host, against one expected-outcome table with two columns:
/// <list type="bullet">
/// <item>the three conversation-keyed routes (<c>GET /conversations/{id}</c>, <c>/{id}/messages</c>, <c>/{id}/history</c>):
/// one decision, identical on the three;</item>
/// <item><c>GET /contacts/{id}/conversations</c>: its own decision, which differs only where the rule says so (an offer
/// alone, Q3; an owner of only a terminal conversation, D5 step 2; the shared contact, D18).</item>
/// </list>
/// The live stream's reader class is then checked against the same <c>ReadsAll</c> column: an audience-less
/// <c>conversation.message</c> reaches the caller's stream exactly when the caller reads every conversation.
/// </summary>
public sealed class ConversationRouteParityTests : IClassFixture<ContentDeliveryApiFactory>
{
    private readonly ContentDeliveryApiFactory _factory;

    public ConversationRouteParityTests(ContentDeliveryApiFactory factory) => _factory = factory;

    /// <summary>The callers of the 2.1 matrix, built on the real host.</summary>
    public enum Caller
    {
        Monitor,
        Read,
        AdminRole,
        HandleWithProfile,
        HandleWithoutProfile,
        NoneOfTheThree,
        ImpersonationMintedRead,
        ImpersonationMintedHandle,
        ImpersonationNothingMinted,
    }

    private static bool ReadsAll(Caller caller) =>
        caller is Caller.Monitor or Caller.Read or Caller.AdminRole or Caller.ImpersonationMintedRead;

    /// <summary>
    /// One case: it seeds the world around agent A and returns the conversation read and its contact, plus what agent A,
    /// a handle holder with its profile, gets on the conversation-keyed routes and on the contact's list.
    /// </summary>
    private sealed record ParityCase(string Name, Func<Agent, (Conversation Conversation, Contact Contact)> Seed, bool AReadsConversation, bool AReadsList);

    private ParityCase[] Cases() =>
    [
        new("owned by A, Active", a =>
        {
            var k = PerSenderContact();
            return (Save(k, ConversationOwner.ForAgent(a.AgentId), ConversationState.Active), k);
        }, true, true),
        new("owned by A, Closed, A's only one of K (D5 step 2)", a =>
        {
            var k = PerSenderContact();
            return (Save(k, ConversationOwner.ForAgent(a.AgentId), ConversationState.Closed), k);
        }, true, false),
        new("offered to A, the only one of K (Q3)", a =>
        {
            var k = PerSenderContact();
            return (_factory.SeedOfferTo(a, _factory.SeedQueue(), contactId: k.ContactId), k);
        }, true, false),
        .. new[] { ConversationState.Active, ConversationState.Resolved, ConversationState.WrapUp, ConversationState.WaitingForCustomer }
            .Select(state => new ParityCase($"B's closed one of K while A holds K in {state}", a =>
            {
                var k = PerSenderContact();
                Save(k, ConversationOwner.ForAgent(a.AgentId), state);
                return (Save(k, ConversationOwner.ForAgent(AgentB().AgentId), ConversationState.Closed), k);
            }, true, true)),
        new("B's closed one of K while A is only offered one of K", a =>
        {
            var k = PerSenderContact();
            _factory.SeedOfferTo(a, _factory.SeedQueue(), contactId: k.ContactId);
            return (Save(k, ConversationOwner.ForAgent(AgentB().AgentId), ConversationState.Closed), k);
        }, false, false),
        new("B's active one of K while A owns only a closed one of K", a =>
        {
            var k = PerSenderContact();
            Save(k, ConversationOwner.ForAgent(a.AgentId), ConversationState.Closed);
            return (Save(k, ConversationOwner.ForAgent(AgentB().AgentId), ConversationState.Active), k);
        }, false, false),
        new("A's one of K, transferred away to another agent", a =>
        {
            var k = PerSenderContact();
            var moved = Save(k, ConversationOwner.ForAgent(a.AgentId), ConversationState.Active);
            moved.Owner = ConversationOwner.ForAgent(AgentB().AgentId);
            using (var scope = _factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<IConversationStore>().SaveAsync(moved, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            return (moved, k);
        }, false, false),
        new("B's closed one of K while A holds nothing of K", a =>
        {
            _ = a;
            var k = PerSenderContact();
            return (Save(k, ConversationOwner.ForAgent(AgentB().AgentId), ConversationState.Closed), k);
        }, false, false),
        new("B's closed call of the shared anonymous contact while A owns an active one (D18)", a =>
        {
            var s = _factory.SeedContact(SharedContactAddresses.AnonymousVoice);
            Save(s, ConversationOwner.ForAgent(a.AgentId), ConversationState.Active, ChannelType.Voice);
            return (Save(s, ConversationOwner.ForAgent(AgentB().AgentId), ConversationState.Closed, ChannelType.Voice), s);
        }, false, true),
        new("A's own active call of the shared anonymous contact", a =>
        {
            var s = _factory.SeedContact(SharedContactAddresses.AnonymousVoice);
            return (Save(s, ConversationOwner.ForAgent(a.AgentId), ConversationState.Active, ChannelType.Voice), s);
        }, true, true),
    ];

    private Contact PerSenderContact() =>
        _factory.SeedContact(new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}"));

    private Agent AgentB() => _factory.NewAgent("parity-b").Agent;

    private Conversation Save(Contact contact, ConversationOwner owner, ConversationState state, ChannelType channel = ChannelType.WebChat) =>
        _factory.SeedConversation(owner, state, channel: channel, contactId: contact.ContactId);

    /// <summary>
    /// The caller's client and agent A, the profile the cases are seeded around: the caller's own profile when it holds
    /// one (handle with profile, none of the three), otherwise a fresh agent the caller is not.
    /// </summary>
    private (HttpClient Client, Agent AgentA) Arrange(Caller caller)
    {
        switch (caller)
        {
            case Caller.HandleWithProfile:
            {
                var (user, agent) = _factory.NewAgent("parity-a");
                return (_factory.ClientFor(user), agent);
            }

            case Caller.NoneOfTheThree:
            {
                var (user, agent) = _factory.NewAgent("parity-none");
                _factory.GrantTemplate(user, "no-such-template"); // no permission at all, A's profile kept
                return (_factory.ClientFor(user), agent);
            }
        }

        var agentA = _factory.NewAgent("parity-a").Agent;
        HttpClient client;
        switch (caller)
        {
            case Caller.Monitor:
            {
                var user = _factory.NewUser(UserRole.Agent, "parity-manager");
                _factory.GrantTemplate(user, "manager");
                client = _factory.ClientFor(user);
                break;
            }

            case Caller.Read:
            {
                var user = _factory.NewUser(UserRole.Agent, "parity-qa");
                _factory.GrantTemplate(user, "quality_analyst");
                client = _factory.ClientFor(user);
                break;
            }

            case Caller.AdminRole:
                client = _factory.ClientFor(_factory.NewUser(UserRole.Admin, "parity-admin"));
                break;
            case Caller.HandleWithoutProfile:
                client = _factory.ClientFor(_factory.NewUser(UserRole.Agent, "parity-no-profile"));
                break;
            case Caller.ImpersonationMintedRead:
                client = Impersonating(ConversationReaderClass.ReadPermission);
                break;
            case Caller.ImpersonationMintedHandle:
                client = Impersonating(ConversationReaderClass.HandlePermission);
                break;
            case Caller.ImpersonationNothingMinted:
                client = Impersonating("contacts:contact:view");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caller));
        }

        return (client, agentA);
    }

    private HttpClient Impersonating(string minted) =>
        _factory.CreateBearerClient(_factory.MintImpersonationToken(_factory.NewPlatformAdmin(), [minted], readOnly: true));

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        return response.StatusCode;
    }

    [Theory]
    [InlineData(Caller.Monitor)]
    [InlineData(Caller.Read)]
    [InlineData(Caller.AdminRole)]
    [InlineData(Caller.HandleWithProfile)]
    [InlineData(Caller.HandleWithoutProfile)]
    [InlineData(Caller.NoneOfTheThree)]
    [InlineData(Caller.ImpersonationMintedRead)]
    [InlineData(Caller.ImpersonationMintedHandle)]
    [InlineData(Caller.ImpersonationNothingMinted)]
    public async Task GovernedRoutes_ShouldDecideAsTheParityTableSays_ForEveryCaseOfTheCaller(Caller caller)
    {
        var (client, agentA) = Arrange(caller);
        using var _ = client;
        var mismatches = new List<string>();
        var rows = 0;

        foreach (var parityCase in Cases())
        {
            var (conversation, contact) = parityCase.Seed(agentA);
            var id = conversation.ConversationId.Value;
            var readsConversation = ReadsAll(caller) || (caller == Caller.HandleWithProfile && parityCase.AReadsConversation);
            var readsList = ReadsAll(caller) || (caller == Caller.HandleWithProfile && parityCase.AReadsList);
            var expectedConversation = readsConversation ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
            var expectedList = readsList ? HttpStatusCode.OK : HttpStatusCode.Forbidden;

            foreach (var route in new[]
                     {
                         $"/api/v1/conversations/{id}",
                         $"/api/v1/conversations/{id}/messages",
                         $"/api/v1/conversations/{id}/history",
                     })
            {
                rows++;
                var actual = await StatusAsync(client, route);
                if (actual != expectedConversation)
                    mismatches.Add($"{parityCase.Name}: GET {route} gave {(int)actual}, the table says {(int)expectedConversation}");
            }

            rows++;
            var listRoute = $"/api/v1/contacts/{contact.ContactId.Value}/conversations";
            var list = await StatusAsync(client, listRoute);
            if (list != expectedList)
                mismatches.Add($"{parityCase.Name}: GET {listRoute} gave {(int)list}, the table says {(int)expectedList}");
        }

        foreach (var route in new[]
                 {
                     "/api/v1/conversations/conv-missing-parity",
                     "/api/v1/conversations/conv-missing-parity/messages",
                     "/api/v1/conversations/conv-missing-parity/history",
                     "/api/v1/contacts/contact-missing-parity/conversations",
                 })
        {
            rows++;
            var actual = await StatusAsync(client, route);
            if (actual != HttpStatusCode.NotFound)
                mismatches.Add($"missing id: GET {route} gave {(int)actual}, the table says 404 (D2 step 1)");
        }

        rows.Should().Be((Cases().Length * 4) + 4, "every case is driven through the four governed routes");
        mismatches.Should().BeEmpty($"{caller} must get the table's decision on every row");
    }

    [Theory]
    [InlineData(Caller.Monitor)]
    [InlineData(Caller.Read)]
    [InlineData(Caller.AdminRole)]
    [InlineData(Caller.HandleWithProfile)]
    [InlineData(Caller.HandleWithoutProfile)]
    [InlineData(Caller.NoneOfTheThree)]
    [InlineData(Caller.ImpersonationMintedRead)]
    [InlineData(Caller.ImpersonationMintedHandle)]
    [InlineData(Caller.ImpersonationNothingMinted)]
    public async Task StreamEvents_ShouldDeliverAnUnaddressedMessage_ExactlyWhenTheRuleReadsAllForTheCaller(Caller caller)
    {
        var (client, agentA) = Arrange(caller);
        var unrelated = Save(PerSenderContact(), ConversationOwner.ForAgent(AgentB().AgentId), ConversationState.Closed);
        var restRead = await StatusAsync(client, $"/api/v1/conversations/{unrelated.ConversationId.Value}") == HttpStatusCode.OK;
        _ = agentA;

        await using var stream = await LiveStreamProbe.OpenAsync($"{caller}", client);
        await LiveStreamProbe.SyncAsync(_factory.Bus, ConversationOwnershipApiFactory.Tenant.Value, stream);
        var messageId = $"m-parity-{Guid.NewGuid():N}";
        _factory.Bus.Publish(new ConversationMessageEvent(
                ConversationOwnershipApiFactory.Tenant.Value, unrelated.ConversationId.Value, messageId, "Inbound", "WebChat")
            { Audience = ConversationAudience.None });
        await LiveStreamProbe.SyncAsync(_factory.Bus, ConversationOwnershipApiFactory.Tenant.Value, stream);

        restRead.Should().Be(ReadsAll(caller), $"the REST rule reads a stranger's conversation exactly when {caller} reads all");
        stream.Received(messageId).Should().Be(restRead,
            $"the stream's reader class must agree with the REST rule's ReadsAll for {caller} (design D17)");
    }
}
