using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Tests.OutboundTrunk;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Storage.InMemory;
using Verbara.Sdk.Ami.Actions;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Dialer.Execution;
using Verbara.Sdk.Pro.MultiTenant;
using static Verbara.Platform.Api.Tests.OutboundTrunk.TrunkFakes;

namespace Verbara.Platform.Api.Tests.Contacts;

/// <summary>
/// contacts-writes-follow-their-permissions, task 1.2 (dial side): regression tests written first, against the unfixed
/// code (design D10, D13; fixtures/do-not-contact-refusals.v1.json). Agent click-to-dial refuses with
/// <c>contact-do-not-contact</c>, else <c>contact-consent-withdrawn</c>, when the contact <c>contactId</c> names or any
/// contact whose stored Voice address has the dialed number's canonical form is restricted; it sends no Originate,
/// creates no contact, and fails closed on a contact-store error. The contacts live in the real in-memory store.
/// </summary>
public sealed class DialDoNotContactTests
{
    private const string Tenant = "acme";
    private static readonly TenantId TenantId = new(Tenant);
    private static readonly JsonObject Fixture = ContactWritesApiFactory.Fixture("do-not-contact-refusals.v1.json");

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IAgentCapacityService _capacity = Substitute.For<IAgentCapacityService>();
    private readonly IContactIdentityResolver _resolver = Substitute.For<IContactIdentityResolver>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly RecordingOriginateExecutor _executor = new();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly EntityId _agentId = EntityId.New();
    private readonly FakeTrunkStore _trunks = new FakeTrunkStore().Add(Tenant, IpTrunk(7, "pstn-default", "198.51.100.7"));
    private IContactStore _store = new InMemoryContactStore();

    public DialDoNotContactTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        _agents.GetByIdAsync(TenantId, _agentId, Arg.Any<CancellationToken>()).Returns(new Agent
        {
            AgentId = _agentId, TenantId = TenantId, UserId = EntityId.New(),
            DisplayName = "A", State = AgentState.Available, CreatedAt = DateTimeOffset.UnixEpoch,
        });
        _resolver.ResolveAsync(TenantId, Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>())
            .Returns(new Contact { ContactId = EntityId.New(), TenantId = TenantId, CreatedAt = DateTimeOffset.UnixEpoch });
        _tenants.GetAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new Tenant { TenantId = Tenant, Name = "Acme", Options = new TenantOptions() });
    }

    private AgentOutboundDialService CreateService()
    {
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(true);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        return new AgentOutboundDialService(
            _conversations, _capacity, _resolver, _store, _agents, _tenants, _executor, Resolver(null, _trunks, "pstn-default"),
            null, leader, _clock, NullLogger<AgentOutboundDialService>.Instance);
    }

    private Contact Save(string contactId, bool doNotContact, string voiceAddress, IReadOnlyDictionary<string, bool>? consent = null)
    {
        var contact = new Contact { ContactId = EntityId.From(contactId), TenantId = TenantId, CreatedAt = DateTimeOffset.UnixEpoch };
        contact.DoNotContact = doNotContact;
        contact.AddAddress(new ChannelAddress(ChannelType.Voice, voiceAddress));
        foreach (var (channel, value) in consent ?? new Dictionary<string, bool>())
            contact.SetConsent(Enum.Parse<ChannelType>(channel), value);
        _store.SaveAsync(contact, CancellationToken.None).GetAwaiter().GetResult();
        return contact;
    }

    private static Dictionary<string, bool> ConsentOf(JsonNode? node) =>
        node is null ? new Dictionary<string, bool>() : node.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<bool>());

    private static (string ToNumber, EntityId? ContactId) Body(JsonNode request)
    {
        var body = request["body"]!;
        var contactId = body["contactId"]?.GetValue<string>();
        return (body["toNumber"]!.GetValue<string>(), contactId is null ? null : EntityId.From(contactId));
    }

    private async Task ShouldRefuseAsync(string toNumber, EntityId? contactId, string error)
    {
        var outcome = await CreateService().DialAsync(TenantId, _agentId, toNumber, contactId, CancellationToken.None);

        var named = contactId is { } id ? id.Value : "no contact";
        outcome.Accepted.Should().BeFalse($"a dial of {toNumber} naming {named} is refused");
        outcome.Error.Should().Be(error);
        _executor.Actions.Should().BeEmpty("a refused dial sends no Originate");
        await _resolver.DidNotReceive().ResolveAsync(Arg.Any<TenantId>(), Arg.Any<ChannelAddress>(), Arg.Any<CancellationToken>());
        await _conversations.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task DialAsync_ShouldRefuseDoNotContact_WhenTheNamedContactIsMarked()
    {
        var dial = Fixture["dial"]!;
        var (toNumber, contactId) = Body(dial["request"]!);
        Save(contactId!.Value, doNotContact: true, toNumber);

        await ShouldRefuseAsync(toNumber, contactId, dial["body"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task DialAsync_ShouldRefuseDoNotContact_WhenTheNumberIsAMarkedContactsAndTheNamedContactIsAnother()
    {
        var dial = Fixture["dialNumberOfMarkedContactWithUnrelatedContactId"]!;
        var (toNumber, contactId) = Body(dial["request"]!);
        Save(Body(Fixture["dial"]!["request"]!).ContactId!.Value, doNotContact: true, toNumber);
        Save(contactId!.Value, doNotContact: false, "+15550000001");

        await ShouldRefuseAsync(toNumber, contactId, dial["body"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task DialAsync_ShouldRefuseDoNotContact_WhenTheNumberIsAMarkedContactsAndTheNamedContactIsUnknown()
    {
        var dial = Fixture["dialNumberOfMarkedContactWithUnknownContactId"]!;
        var (toNumber, contactId) = Body(dial["request"]!);
        Save(Body(Fixture["dial"]!["request"]!).ContactId!.Value, doNotContact: true, toNumber);

        await ShouldRefuseAsync(toNumber, contactId, dial["body"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task DialAsync_ShouldRefuseDoNotContact_WhenTheNumberIsAMarkedContactsAndNoContactIsNamed()
    {
        var (toNumber, contactId) = Body(Fixture["dial"]!["request"]!);
        Save(contactId!.Value, doNotContact: true, toNumber);

        await ShouldRefuseAsync(toNumber, contactId: null, Fixture["dial"]!["body"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task DialAsync_ShouldRefuseConsentWithdrawn_WhenTheNamedContactRecordsVoiceFalse()
    {
        var dial = Fixture["dialConsentWithdrawn"]!;
        var (toNumber, contactId) = Body(dial["request"]!);
        Save(contactId!.Value, doNotContact: false, toNumber, ConsentOf(dial["contactChannelConsent"]));

        await ShouldRefuseAsync(toNumber, contactId, dial["body"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task DialAsync_ShouldRefuseDoNotContact_WhenTheContactIsMarkedAndRecordsVoiceFalse()
    {
        var (toNumber, contactId) = Body(Fixture["dial"]!["request"]!);
        Save(contactId!.Value, doNotContact: true, toNumber, new Dictionary<string, bool> { ["Voice"] = false });

        await ShouldRefuseAsync(toNumber, contactId, Fixture["dial"]!["body"]!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task DialAsync_ShouldOriginate_WhenVoiceConsentIsAbsentOrRecordedTrue(bool? voice)
    {
        var (toNumber, contactId) = Body(Fixture["dial"]!["request"]!);
        Save(contactId!.Value, doNotContact: false, toNumber,
            voice is null ? null : new Dictionary<string, bool> { ["Voice"] = voice.Value });

        var outcome = await CreateService().DialAsync(TenantId, _agentId, toNumber, contactId, CancellationToken.None);

        outcome.Accepted.Should().BeTrue(outcome.Error);
        _executor.Actions.Should().ContainSingle();
    }

    [Fact]
    public async Task DialAsync_ShouldFailWithNoOriginate_WhenTheContactStoreFails()
    {
        var failing = Substitute.For<IContactStore>();
        failing.GetByIdAsync(default, default!, default).ReturnsForAnyArgs<Contact?>(_ => throw new InvalidOperationException("contact store down"));
        _store = failing;
        var (toNumber, contactId) = Body(Fixture["dial"]!["request"]!);

        var dial = () => CreateService().DialAsync(TenantId, _agentId, toNumber, contactId, CancellationToken.None);

        await dial.Should().ThrowAsync<InvalidOperationException>("a contact-store error fails the dial (fail closed)");
        _executor.Actions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("dialSharedNumberDoNotContact", true)]
    [InlineData("dialSharedNumberDoNotContact", false)]
    [InlineData("dialSharedNumberConsentWithdrawn", true)]
    [InlineData("dialSharedNumberConsentWithdrawn", false)]
    public async Task DialAsync_ShouldRefuseWhicheverContactTheStoreListsFirst_WhenSeveralContactsHoldTheNumber(
        string fixtureCase, bool restrictedSavedFirst)
    {
        var shared = Fixture[fixtureCase]!;
        var contacts = shared["contacts"]!.AsArray().Select(c => c!).ToList();
        var restricted = contacts.Single(c => c["doNotContact"]!.GetValue<bool>() || ConsentOf(c["channelConsent"]).ContainsValue(false));
        var order = restrictedSavedFirst ? contacts.OrderBy(c => c == restricted ? 0 : 1) : contacts.OrderBy(c => c == restricted ? 1 : 0);
        foreach (var c in order)
        {
            Save(c["contactId"]!.GetValue<string>(), c["doNotContact"]!.GetValue<bool>(),
                c["addresses"]![0]!["address"]!.GetValue<string>(), ConsentOf(c["channelConsent"]));
        }

        foreach (var request in shared["requests"]!.AsArray())
        {
            var (toNumber, contactId) = Body(request!);
            await ShouldRefuseAsync(toNumber, contactId, shared["body"]!["error"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task DialAsync_ShouldRefuseDoNotContact_WhenTheStoredAddressIsWrittenDifferently()
    {
        var formatted = Fixture["dialStoredFormattedAddress"]!;
        const string unrelated = "0b9d8c7e6f5a4b3c2d1e0f9a8b7c6d5e";
        Save(unrelated, doNotContact: false, "+15550000002");

        foreach (var example in formatted["examples"]!.AsArray())
        {
            var marked = Save($"marked-{Guid.NewGuid():N}", doNotContact: true, example!["storedAddress"]!.GetValue<string>());
            var toNumber = example["toNumber"]!.GetValue<string>();

            await ShouldRefuseAsync(toNumber, contactId: null, example["error"]!.GetValue<string>());
            await ShouldRefuseAsync(toNumber, EntityId.From(unrelated), example["error"]!.GetValue<string>());

            await _store.DeleteAsync(TenantId, marked.ContactId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DialAsync_ShouldNotRefuseByNumber_WhenOnlyThePresenceOfALeadingPlusDiffers()
    {
        // The pinned residual (dialCanonicalFormResidual): a later country rule flips this on purpose.
        foreach (var example in Fixture["dialCanonicalFormResidual"]!["examples"]!.AsArray())
        {
            var marked = Save($"marked-{Guid.NewGuid():N}", doNotContact: true, example!["storedAddress"]!.GetValue<string>());
            _executor.Actions.Clear();

            var outcome = await CreateService().DialAsync(
                TenantId, _agentId, example["toNumber"]!.GetValue<string>(), null, CancellationToken.None);

            example["refusedByNumberSideCheck"]!.GetValue<bool>().Should().BeFalse();
            outcome.Accepted.Should().BeTrue(outcome.Error);
            _executor.Actions.Should().ContainSingle();
            await _store.DeleteAsync(TenantId, marked.ContactId, CancellationToken.None);
        }
    }

    private sealed class RecordingOriginateExecutor : OriginateExecutorBase
    {
        public List<OriginateAction> Actions { get; } = [];

        protected override ValueTask<OriginateResult> ExecuteCoreAsync(OriginateAction action, string nodeId, CancellationToken ct)
        {
            Actions.Add(action);
            return ValueTask.FromResult(new OriginateResult(true, "act-1", null));
        }
    }
}
