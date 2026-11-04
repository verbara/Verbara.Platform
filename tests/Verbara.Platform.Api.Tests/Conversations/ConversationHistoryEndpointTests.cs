using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// contact-history-opens-by-assignment task 2.4 over the real host (design D10, D13, D14):
/// <c>GET /api/v1/conversations/{id}/history</c> answers <c>MessageHistoryPageDto</c> through the app's source-generated
/// serializer options, member for member with <c>conversation-message-history.v1.json</c>; newest first with a keyset
/// cursor that never repeats or skips a message; an empty page for a conversation without messages; 400
/// <c>invalid-cursor</c> for a forged cursor; a cursor of another conversation reveals nothing; <c>limit</c> clamped to
/// 1..100; 404 before the read rule and 403 <c>not-readable</c> from it.
/// </summary>
public sealed class ConversationHistoryEndpointTests : IClassFixture<ConversationOwnershipApiFactory>
{
    private static readonly TenantId Tenant = ConversationOwnershipApiFactory.Tenant;
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly ConversationOwnershipApiFactory _factory;

    public ConversationHistoryEndpointTests(ConversationOwnershipApiFactory factory) => _factory = factory;

    private static string HistoryRoute(EntityId conversationId) => $"/api/v1/conversations/{conversationId.Value}/history";

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    private static JsonObject Fixture()
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", "conversation-message-history.v1.json");
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    /// <summary>
    /// The fixture's body with its annotations (<c>_omittedWhileNull</c>) removed, its tenant replaced with this host's,
    /// and every date-time rewritten to one form, so an instant compares by value, not by its spelling.
    /// </summary>
    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .Where(member => !member.Key.StartsWith('_'))
            .Select(member => KeyValuePair.Create(member.Key, member.Key == "tenantId"
                ? JsonValue.Create(Tenant.Value)
                : Normalize(member.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Normalize)]),
        JsonValue value when value.TryGetValue<string>(out var text)
            && text.Length >= 20 && text[4] == '-' && text[10] == 'T'
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) =>
            JsonValue.Create(instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        _ => node?.DeepClone(),
    };

    private static async Task<JsonObject> OkBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"the response was {body}");
        return JsonNode.Parse(body)!.AsObject();
    }

    private HttpClient SupervisorClient(string label) => _factory.ClientFor(_factory.NewUser(UserRole.Supervisor, label));

    private Conversation SeedClosed(string? id = null)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From(id ?? $"conv-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Closed,
            Owner = ConversationOwner.ForAgent(EntityId.From($"agent-{Guid.NewGuid():N}")),
            CreatedAt = T0,
            ClosedAt = T0.AddDays(3),
        };
        _factory.Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None)
            .GetAwaiter().GetResult();
        return conversation;
    }

    private Message Save(Message message)
    {
        _factory.Services.GetRequiredService<IMessageStore>().SaveAsync(message, CancellationToken.None)
            .GetAwaiter().GetResult();
        return message;
    }

    private Message SaveText(Conversation conversation, DateTimeOffset at, string? id = null) => Save(new Message
    {
        MessageId = EntityId.From(id ?? $"m-{Guid.NewGuid():N}"),
        ConversationId = conversation.ConversationId,
        TenantId = Tenant,
        Direction = MessageDirection.Inbound,
        Channel = conversation.Channel,
        Content = new MessageEnvelope([new TextBlock($"at {at:O}")]),
        DeliveryStatus = MessageDeliveryStatus.Delivered,
        CreatedAt = at,
    });

    private List<Message> SeedMessages(Conversation conversation, int count) =>
        [.. Enumerable.Range(0, count).Select(i => SaveText(conversation, T0.AddSeconds(i)))];

    private static IEnumerable<string> IdsOf(JsonObject page) =>
        page["items"]!.AsArray().Select(item => item!["messageId"]!.GetValue<string>());

    private static string WithBefore(string route, JsonObject page, string separator = "?") =>
        $"{route}{separator}before={Uri.EscapeDataString(page["nextBefore"]!.GetValue<string>())}";

    // ─── The wire shape ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_ShouldAnswerTheFixtureMemberForMember_WhenAReaderPagesTheTranscript()
    {
        var fixture = Fixture();
        var conversation = SeedClosed("4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a");
        Save(new Message
        {
            MessageId = EntityId.From("m-0001-f0e1d2c3b4a59687"),
            ConversationId = conversation.ConversationId,
            TenantId = Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([new TextBlock("Hello")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = "wamid.EXAMPLE0001",
            CreatedAt = At("2026-10-28T09:15:00Z"),
        });
        Save(new Message
        {
            MessageId = EntityId.From("m-0002-a1b2c3d4e5f6a7b8"),
            ConversationId = conversation.ConversationId,
            TenantId = Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([new TextBlock("My card was blocked after I reported it lost")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            ExternalMessageId = "wamid.EXAMPLE0002",
            CreatedAt = At("2026-10-28T09:16:30Z"),
        });
        Save(new Message
        {
            MessageId = EntityId.From("m-0003-b1c2d3e4f5a6b7c8"),
            ConversationId = conversation.ConversationId,
            TenantId = Tenant,
            Direction = MessageDirection.Outbound,
            Channel = ChannelType.WhatsApp,
            SenderId = "a-9e8d7c6b5a493827",
            Content = new MessageEnvelope([new TextBlock(
                "Your replacement card has been ordered and should arrive within five business days.")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = At("2026-10-28T10:20:05Z"),
            DeliveredAt = At("2026-10-28T10:20:06Z"),
        });
        using var client = SupervisorClient("history-fixture");
        var route = HistoryRoute(conversation.ConversationId);
        var limit = fixture["firstPage"]!["request"]!["query"]!["limit"]!.GetValue<string>();

        var first = await OkBodyAsync(await client.GetAsync($"{route}?limit={limit}"));
        var nextBefore = first["nextBefore"]?.GetValue<string>() ?? string.Empty;
        nextBefore.Should().NotBeEmpty("hasMore is true, so the cursor is present");
        var older = await OkBodyAsync(await client.GetAsync($"{route}?limit={limit}&before={Uri.EscapeDataString(nextBefore)}"));

        // The cursor is opaque: the fixture's placeholder stands for whatever value the server wrote.
        var expectedFirst = Normalize(fixture["firstPage"]!["body"])!.AsObject();
        expectedFirst["nextBefore"] = nextBefore;
        JsonNode.DeepEquals(Normalize(first), expectedFirst).Should().BeTrue(
            $"the first page must equal the fixture member for member.\nexpected: {expectedFirst.ToJsonString()}\nactual:   {Normalize(first)!.ToJsonString()}");
        var expectedOlder = Normalize(fixture["olderPage"]!["body"]);
        JsonNode.DeepEquals(Normalize(older), expectedOlder).Should().BeTrue(
            $"the older page must equal the fixture member for member.\nexpected: {expectedOlder!.ToJsonString()}\nactual:   {Normalize(older)!.ToJsonString()}");
    }

    [Fact]
    public async Task GetHistory_ShouldAnswerAnEmptyPage_WhenTheConversationHasNoMessages()
    {
        var conversation = SeedClosed();
        using var client = SupervisorClient("history-empty");

        var body = await OkBodyAsync(await client.GetAsync(HistoryRoute(conversation.ConversationId)));

        JsonNode.DeepEquals(body, Normalize(Fixture()["emptyConversation"]!["body"]))
            .Should().BeTrue($"an empty conversation answers 200 with an empty page, never 404; the body was {body.ToJsonString()}");
    }

    // ─── Order and paging ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_ShouldAnswerTheFiftyNewestFirstAndPageEveryMessageOnce_WhenAClosedConversationHas120()
    {
        var conversation = SeedClosed();
        var stored = SeedMessages(conversation, 120);
        using var client = SupervisorClient("history-120");
        var route = HistoryRoute(conversation.ConversationId);

        var first = await OkBodyAsync(await client.GetAsync(route));
        var second = await OkBodyAsync(await client.GetAsync(WithBefore(route, first)));
        var third = await OkBodyAsync(await client.GetAsync(WithBefore(route, second)));

        var newestFirst = stored.Select(m => m.MessageId.Value).Reverse().ToList();
        IdsOf(first).Should().Equal(newestFirst.Take(50), "the default limit is 50, newest first");
        first["hasMore"]!.GetValue<bool>().Should().BeTrue();
        IdsOf(second).Should().Equal(newestFirst.Skip(50).Take(50));
        second["hasMore"]!.GetValue<bool>().Should().BeTrue();
        IdsOf(third).Should().Equal(newestFirst.Skip(100));
        third["hasMore"]!.GetValue<bool>().Should().BeFalse();
        third.ContainsKey("nextBefore").Should().BeFalse("nextBefore is absent when hasMore is false");
    }

    [Fact]
    public async Task GetHistory_ShouldRepeatAndSkipNothing_WhenAMessageArrivesBetweenTwoPages()
    {
        var conversation = SeedClosed();
        var stored = SeedMessages(conversation, 6);
        using var client = SupervisorClient("history-race");
        var route = HistoryRoute(conversation.ConversationId);

        var first = await OkBodyAsync(await client.GetAsync($"{route}?limit=3"));
        var arrived = SaveText(conversation, T0.AddMinutes(30));
        var second = await OkBodyAsync(await client.GetAsync(WithBefore($"{route}?limit=3", first, "&")));

        IdsOf(first).Concat(IdsOf(second)).Should().Equal(stored.Select(m => m.MessageId.Value).Reverse());
        IdsOf(second).Should().NotContain(arrived.MessageId.Value);
        second["hasMore"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GetHistory_ShouldBreakEqualTimesByMessageIdDescending_WhenMessagesShareCreatedAt()
    {
        var conversation = SeedClosed();
        var prefix = Guid.NewGuid().ToString("N");
        SaveText(conversation, T0, $"{prefix}-a");
        SaveText(conversation, T0, $"{prefix}-B");
        SaveText(conversation, T0, $"{prefix}-c");
        using var client = SupervisorClient("history-ties");
        var route = HistoryRoute(conversation.ConversationId);

        var all = await OkBodyAsync(await client.GetAsync(route));
        var walked = new List<string>();
        var page = await OkBodyAsync(await client.GetAsync($"{route}?limit=1"));
        walked.AddRange(IdsOf(page));
        while (page["hasMore"]!.GetValue<bool>())
        {
            page = await OkBodyAsync(await client.GetAsync(WithBefore($"{route}?limit=1", page, "&")));
            walked.AddRange(IdsOf(page));
        }

        string[] expected = [$"{prefix}-c", $"{prefix}-a", $"{prefix}-B"];
        IdsOf(all).Should().Equal(expected, "equal createdAt values break by messageId descending, ordinal");
        walked.Should().Equal(expected);
    }

    [Theory]
    [InlineData("limit=0", 1)]
    [InlineData("limit=-5", 1)]
    [InlineData("limit=500", 100)]
    [InlineData("limit=100", 100)]
    [InlineData("", 50)]
    public async Task GetHistory_ShouldClampTheLimit_WhenItIsOutOfRange(string query, int expected)
    {
        var conversation = SeedClosed();
        SeedMessages(conversation, 120);
        using var client = SupervisorClient("history-clamp");

        var body = await OkBodyAsync(await client.GetAsync($"{HistoryRoute(conversation.ConversationId)}?{query}"));

        body["items"]!.AsArray().Should().HaveCount(expected, "limit defaults to 50 and is clamped to 1..100");
        body["hasMore"]!.GetValue<bool>().Should().BeTrue();
    }

    // ─── The cursor ───────────────────────────────────────────────────────────

    public static TheoryData<string> ForgedCursors() => new()
    {
        "not-a-cursor",
        "%25%25%25",
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes("abc:m-1")),
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes("12345")),
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes("12345:")),
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes("-1:m-1")),
    };

    [Theory]
    [MemberData(nameof(ForgedCursors))]
    public async Task GetHistory_ShouldAnswer400InvalidCursor_WhenTheCursorIsForged(string forged)
    {
        var conversation = SeedClosed();
        SeedMessages(conversation, 3);
        using var client = SupervisorClient("history-forged");
        var refusal = Fixture()["invalidCursor"]!;

        using var response = await client.GetAsync($"{HistoryRoute(conversation.ConversationId)}?before={forged}");
        var body = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).Should().Be(refusal["status"]!.GetValue<int>(), because: $"the response was {body}");
        JsonNode.DeepEquals(JsonNode.Parse(body), refusal["body"]).Should().BeTrue(because: $"the response was {body}");
    }

    [Fact]
    public async Task GetHistory_ShouldRevealNothingOfTheOtherConversation_WhenTheCursorCameFromIt()
    {
        var conversation = SeedClosed();
        var other = SeedClosed();
        var mine = SeedMessages(conversation, 4);
        var theirs = Enumerable.Range(0, 4).Select(i => SaveText(other, T0.AddSeconds(i).AddMilliseconds(500))).ToList();
        using var client = SupervisorClient("history-foreign");

        var otherPage = await OkBodyAsync(await client.GetAsync($"{HistoryRoute(other.ConversationId)}?limit=2"));
        var page = await OkBodyAsync(await client.GetAsync(WithBefore(HistoryRoute(conversation.ConversationId), otherPage)));

        // The other conversation's cursor sits at T0+2.5s, so it only bounds this conversation's keyset there.
        IdsOf(page).Should().Equal(mine[2].MessageId.Value, mine[1].MessageId.Value, mine[0].MessageId.Value);
        page["items"]!.AsArray().Should().OnlyContain(item => item!["conversationId"]!.GetValue<string>() == conversation.ConversationId.Value);
        IdsOf(page).Should().NotIntersectWith(theirs.Select(m => m.MessageId.Value));
    }

    // ─── 404 and the read rule ────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_ShouldAnswer404_WhenTheConversationDoesNotExist()
    {
        using var client = SupervisorClient("history-404");

        using var response = await client.GetAsync(HistoryRoute(EntityId.From($"conv-{Guid.NewGuid():N}")));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("404 keeps its empty body (design D13)");
    }

    [Fact]
    public async Task GetHistory_ShouldRefuseNotReadableBeforeReadingTheCursor_WhenTheAgentHoldsNothingOfTheContact()
    {
        var conversation = SeedClosed();
        SeedMessages(conversation, 3);
        var (user, _) = _factory.NewAgent("history-stranger");
        using var client = _factory.ClientFor(user);

        foreach (var query in new[] { "", "?before=not-a-cursor" })
        {
            using var response = await client.GetAsync($"{HistoryRoute(conversation.ConversationId)}{query}");
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, because: $"'{query}' answered {body}");
            JsonNode.DeepEquals(JsonNode.Parse(body), new JsonObject { ["error"] = "not-readable" })
                .Should().BeTrue(because: $"'{query}' answered {body}");
        }
    }

    [Fact]
    public async Task GetHistory_ShouldAnswerTheOwner_WhenItReadsItsOwnClosedConversation()
    {
        var (user, agent) = _factory.NewAgent("history-owner");
        var conversation = _factory.SeedConversation(ConversationOwner.ForAgent(agent.AgentId), ConversationState.Closed);
        var stored = SeedMessages(conversation, 2);
        using var client = _factory.ClientFor(user);

        var body = await OkBodyAsync(await client.GetAsync(HistoryRoute(conversation.ConversationId)));

        IdsOf(body).Should().Equal(stored[1].MessageId.Value, stored[0].MessageId.Value);
    }
}
