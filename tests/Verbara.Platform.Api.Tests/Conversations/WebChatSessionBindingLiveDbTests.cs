using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Services;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// webchat-visitor-identity-two-levels (design D9, spec <c>webchat-visitor-identity</c>): a visitor message stays in its
/// own session's conversation, through the real host over a Testcontainers Postgres. It needs Postgres because the
/// contact-wide read it replaces picks the newest row there (<c>ORDER BY created_at DESC</c>), while the in-memory store
/// reads an unordered dictionary. Each test gets a fresh database and its own host, so row counts start at zero.
/// One contact holding two sessions is seeded through the stores, because <c>CreateSession</c> links nothing yet.
/// </summary>
public sealed class WebChatSessionBindingLiveDbTests
    : IClassFixture<ConversationClockReconcileLiveDbTests.PostgresContainer>
{
    private const string TenantValue = AuthenticatedPlatformApiFactory.TestTenantId;
    private const string SessionAddressKey = "_webchatSessionAddress";
    private static readonly TenantId Tenant = new(TenantValue);
    private static readonly EntityId Queue = EntityId.From("q-session-binding");
    private static readonly EntityId Agent = EntityId.From("agent-session-binding");

    private readonly ConversationClockReconcileLiveDbTests.PostgresContainer _postgres;

    public WebChatSessionBindingLiveDbTests(ConversationClockReconcileLiveDbTests.PostgresContainer postgres) => _postgres = postgres;

    // Case 1. Both conversations are open, and S2's is the newer row.
    [Fact]
    public async Task VisitorMessage_ShouldStayInItsConversation_WhenAnotherSessionOfTheContactIsNewer()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var (s1, s2) = await env.SeedContactWithTwoSessionsAsync();
        var k1 = s1.ConversationId;
        var k2 = s2.ConversationId;
        (await env.ConversationAsync(k2)).CreatedAt.Should().BeAfter((await env.ConversationAsync(k1)).CreatedAt, "K2 is the newer row");

        await env.PostAsync(s1, "de la primera");

        (await env.ConversationsHoldingAsync("de la primera")).Should().Equal([k1.Value],
            "the message belongs to its own session's open conversation, whichever row is newer");
        s1.ConversationId.Should().Be(k1, "the session stays on its conversation");
    }

    // Case 1 over the socket, the other route a visitor message takes.
    [Fact]
    public async Task VisitorSocketMessage_ShouldStayInItsConversation_WhenAnotherSessionOfTheContactIsNewer()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var (s1, _) = await env.SeedContactWithTwoSessionsAsync();
        var k1 = s1.ConversationId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = await env.AttachSocketAsync(s1, timeout.Token);

        var frame = JsonSerializer.Serialize(new WebChatClientMessage("message", "por el socket"), WebChatJsonContext.Default.WebChatClientMessage);
        await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, timeout.Token);

        var holding = await env.WaitForConversationsHoldingAsync("por el socket");
        holding.Should().Equal([k1.Value]);
    }

    // Case 2. S1's conversation ended while S2's is open.
    [Fact]
    public async Task VisitorMessage_ShouldOpenAConversationForItsSession_WhenItsConversationEndedAndAnotherSessionsIsOpen()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var (s1, s2) = await env.SeedContactWithTwoSessionsAsync();
        var k1 = s1.ConversationId;
        var k2 = s2.ConversationId;
        await env.GiveToAgentAsync(k2);
        await env.CloseAsync(k1);
        var k2Before = await env.SignatureAsync(k2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var s1Socket = await env.AttachSocketAsync(s1, timeout.Token);
        using var s2Socket = await env.AttachSocketAsync(s2, timeout.Token);

        await env.PostAsync(s1, "volvi desde la primera");

        var holding = await env.ConversationsHoldingAsync("volvi desde la primera");
        var k3 = EntityId.From(holding.Should().ContainSingle().Subject);
        k3.Should().NotBe(k1).And.NotBe(k2, "another session's open conversation takes no message of this session");
        var opened = await env.ConversationAsync(k3);
        opened.ContactId.Should().Be((await env.ConversationAsync(k1)).ContactId, "the new conversation is the contact's");
        opened.Metadata.Should().Contain(SessionAddressKey, s1.CustomerAddress.Address);
        Env.AssertAssignedToTheQueue(opened);
        (await env.SignatureAsync(k2)).Should().Be(k2Before, "the other session's conversation is unchanged");
        (await env.InboundCountAsync(k2)).Should().Be(0);

        await env.GiveToAgentAsync(k3);
        var reply = await env.ReplyAsync(k3, "respuesta para la primera");

        reply.DeliveryStatus.Should().NotBe(MessageDeliveryStatus.Failed);
        (await ReceiveTextAsync(s1Socket, TimeSpan.FromSeconds(3))).Should().Contain("respuesta para la primera");
        (await ReceiveTextAsync(s2Socket, TimeSpan.FromMilliseconds(500))).Should().BeNull("the reply belongs to S1's session");
    }

    // Case 3. S1's conversation is Resolved while S2's is open.
    [Fact]
    public async Task VisitorMessage_ShouldCloseItsResolvedConversationAndOpenItsOwn_WhenAnotherSessionsIsOpen()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var (s1, s2) = await env.SeedContactWithTwoSessionsAsync();
        var k1 = s1.ConversationId;
        var k2 = s2.ConversationId;
        await env.GiveToAgentAsync(k2);
        await env.ResolveAsync(k1);
        var k2Before = await env.SignatureAsync(k2);

        await env.PostAsync(s1, "volvi a la resuelta");

        var closed = await env.ConversationAsync(k1);
        closed.State.Should().Be(ConversationState.Closed, "the session's own Resolved conversation closes on the visitor's return");
        closed.Metadata.Should().Contain(ConversationCloseReasons.MetadataKey, ConversationCloseReasons.CustomerReturned);
        env.ReturnCloseEventsOf(k1).Should().Be(1, "the close is announced once");
        env.ReturnCloseAuditsOf(k1).Should().Be(1);

        var holding = await env.ConversationsHoldingAsync("volvi a la resuelta");
        var k3 = EntityId.From(holding.Should().ContainSingle().Subject);
        k3.Should().NotBe(k1).And.NotBe(k2);
        var opened = await env.ConversationAsync(k3);
        opened.Metadata.Should().Contain(SessionAddressKey, s1.CustomerAddress.Address);
        Env.AssertAssignedToTheQueue(opened);
        (await env.SignatureAsync(k2)).Should().Be(k2Before, "the other session's conversation is unchanged");
    }

    // Case 4. S2 and K2 first, then S1 and K1, so K1 is the newer row: the contact-wide read returns it.
    [Fact]
    public async Task VisitorMessage_ShouldLeaveAnotherSessionsResolvedConversationAlone_WhenThatConversationIsNewer()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var (s2, s1) = await env.SeedContactWithTwoSessionsAsync();
        var k1 = s1.ConversationId;
        var k2 = s2.ConversationId;
        (await env.ConversationAsync(k1)).CreatedAt.Should().BeAfter((await env.ConversationAsync(k2)).CreatedAt, "K1 is the newer row on purpose");
        await env.ResolveAsync(k1);
        var k1Before = await env.SignatureAsync(k1);

        await env.PostAsync(s2, "de la segunda");

        (await env.ConversationsHoldingAsync("de la segunda")).Should().Equal([k2.Value]);
        (await env.ConversationAsync(k1)).State.Should().Be(ConversationState.Resolved, "this session's message does not close another session's conversation");
        (await env.SignatureAsync(k1)).Should().Be(k1Before);
        env.ReturnCloseEventsOf(k1).Should().Be(0, "no close is announced");
        env.ReturnCloseAuditsOf(k1).Should().Be(0);
    }

    // Case 5, regression. The address list plays no part in attributing a session's messages.
    [Fact]
    public async Task VisitorMessage_ShouldKeepTheSessionsContact_WhenItsAddressWasRemovedFromTheContact()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var session = await env.CreateSessionAsync();
        var k1 = session.ConversationId;
        var c1 = (await env.ConversationAsync(k1)).ContactId;
        await env.ReplaceAddressesAsync(c1, new ChannelAddress(ChannelType.WhatsApp, "+573001112233"));
        var contactBefore = await env.ContactSignatureAsync(c1);

        await env.PostAsync(session, "sin direccion");

        (await env.ContactCountAsync()).Should().Be(1L, "the session's contact still exists, so no contact is created");
        (await env.ConversationsHoldingAsync("sin direccion")).Should().Equal([k1.Value]);
        (await env.ConversationAsync(k1)).ContactId.Should().Be(c1);
        (await env.ContactSignatureAsync(c1)).Should().Be(contactBefore, "nothing is written to the contact");
    }

    // Case 5, preservation control: green before the fix. The binding's fallback must keep it.
    [Fact]
    public async Task VisitorMessage_ShouldCreateOneContactHoldingOnlyTheSessionAddress_WhenTheContactWasDeleted()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var session = await env.CreateSessionAsync();
        var c1 = (await env.ConversationAsync(session.ConversationId)).ContactId;
        await env.Contacts.DeleteAsync(Tenant, c1, CancellationToken.None);

        await env.PostAsync(session, "tras el borrado");

        (await env.ContactCountAsync()).Should().Be(1L);
        (await env.Contacts.GetByIdAsync(Tenant, c1, CancellationToken.None)).Should().BeNull("the deleted contact is not brought back");
        var holding = await env.ConversationsHoldingAsync("tras el borrado");
        var c2 = (await env.ConversationAsync(EntityId.From(holding.Should().ContainSingle().Subject))).ContactId;
        c2.Should().NotBe(c1);
        (await env.Contacts.GetByIdAsync(Tenant, c2, CancellationToken.None))!.Addresses.Should().Equal([session.CustomerAddress]);
    }

    // Case 6, preservation control with one assertion of task 2.1's: green before the fix. The bound path is what could
    // land in K1, so its power comes from removing the ContactId comparison afterwards. Reads the stores, not the result.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisitorMessage_ShouldOpenAConversationOfTheNewContact_WhenTheContactWasDeletedAndItsConversationStays(bool resolved)
    {
        await using var env = await Env.CreateAsync(_postgres);
        var session = await env.CreateSessionAsync();
        var k1 = session.ConversationId;
        var c1 = (await env.ConversationAsync(k1)).ContactId;
        await env.PostAsync(session, "antes del borrado");
        if (resolved)
            await env.ResolveAsync(k1);
        var k1Before = await env.SignatureAsync(k1);
        var k1State = (await env.ConversationAsync(k1)).State;

        await env.DeleteContactThroughTheRouteAsync(c1);
        (await env.Contacts.GetByIdAsync(Tenant, c1, CancellationToken.None)).Should().BeNull();
        ConversationStateMachine.IsTerminal((await env.ConversationAsync(k1)).State).Should().BeFalse("the delete leaves K1 open");
        (await env.ConversationAsync(k1)).ContactId.Should().Be(c1, "the delete leaves K1 naming the deleted contact");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = await env.AttachSocketAsync(session, timeout.Token);

        await env.PostAsync(session, "tras el borrado");

        var holding = await env.ConversationsHoldingAsync("tras el borrado");
        var k3 = EntityId.From(holding.Should().ContainSingle().Subject);
        k3.Should().NotBe(k1, "K1 belongs to the deleted contact");
        var opened = await env.ConversationAsync(k3);
        var c2 = opened.ContactId;
        c2.Should().NotBe(c1);
        (await env.Contacts.GetByIdAsync(Tenant, c2, CancellationToken.None))!.Addresses.Should().Equal([session.CustomerAddress]);
        (await env.ContactCountAsync()).Should().Be(1L);
        Env.AssertAssignedToTheQueue(opened);
        env.RoutingsOf(k3).Should().Be(1, "routed once");
        opened.Metadata.Should().Contain(SessionAddressKey, session.CustomerAddress.Address);

        (await env.SignatureAsync(k1)).Should().Be(k1Before, "K1 keeps its state, its contact and its messages");
        (await env.ConversationAsync(k1)).State.Should().Be(k1State);
        env.ReturnCloseEventsOf(k1).Should().Be(0, "nothing is announced for K1");
        env.ReturnCloseAuditsOf(k1).Should().Be(0);

        await env.GiveToAgentAsync(k3);
        var reply = await env.ReplyAsync(k3, "respuesta tras el borrado");
        reply.DeliveryStatus.Should().NotBe(MessageDeliveryStatus.Failed);
        (await ReceiveTextAsync(socket, TimeSpan.FromSeconds(3))).Should().Contain("respuesta tras el borrado");

        await env.PostAsync(session, "otra mas");
        (await env.ConversationsHoldingAsync("otra mas")).Should().Equal([k3.Value], "the session's next message is stored in K3");
        (await env.ContactCountAsync()).Should().Be(1L);
    }

    // The contact half of the rebind, which no store shows while the address still finds the new contact.
    [Fact]
    public async Task VisitorMessage_ShouldRebindTheSessionToTheNewContact_WhenTheContactWasDeleted()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var session = await env.CreateSessionAsync();
        var c1 = (await env.ConversationAsync(session.ConversationId)).ContactId;
        await env.DeleteContactThroughTheRouteAsync(c1);

        await env.PostAsync(session, "tras el borrado");

        var holding = await env.ConversationsHoldingAsync("tras el borrado");
        var k3 = EntityId.From(holding.Should().ContainSingle().Subject);
        session.ConversationId.Should().Be(k3);
        session.ContactId.Should().Be((await env.ConversationAsync(k3)).ContactId);
    }

    // Preservation control: green before the fix. The router's read of the new conversation fails: nothing is routed,
    // the session is already rebound, and its next message claims that conversation and routes it.
    [Fact]
    public async Task VisitorMessage_ShouldRouteOnTheNextMessage_WhenTheReadOfTheConversationItOpenedFailed()
    {
        await using var env = await Env.CreateAsync(_postgres);
        var session = await env.CreateSessionAsync();
        var k1 = session.ConversationId;
        await env.CloseAsync(k1);
        var faults = 0;
        env.Store.GetByIdFault = id =>
            id != k1 && Interlocked.Increment(ref faults) == 1 ? new InvalidOperationException("database unavailable") : null;

        await env.PostAsync(session, "con la lectura caida");

        faults.Should().BeGreaterThan(0, "the router read the conversation the message opened");
        env.Store.GetByIdFault = null;
        var holding = await env.ConversationsHoldingAsync("con la lectura caida");
        var k3 = EntityId.From(holding.Should().ContainSingle().Subject);
        k3.Should().NotBe(k1);
        session.ConversationId.Should().Be(k3, "the rebind runs before the read");
        var unrouted = await env.ConversationAsync(k3);
        unrouted.State.Should().Be(ConversationState.Queued);
        unrouted.Owner.Should().BeNull("nothing was routed");

        await env.PostAsync(session, "ya sin fallo");

        (await env.ConversationsHoldingAsync("ya sin fallo")).Should().Equal([k3.Value], "the next message claims that conversation");
        Env.AssertAssignedToTheQueue(await env.ConversationAsync(k3));
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        var buffer = new byte[8192];
        try
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(buffer, 0, result.Count) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The real host over <paramref name="ConnectionString"/>; its conversation store is hooked so a test can fail one read.</summary>
    private sealed class BoundHost(string ConnectionString) : AuthenticatedPlatformApiFactory
    {
        public IAuditService Audit { get; } = Substitute.For<IAuditService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureServices((_, services) =>
            {
                services.AddSingleton(Audit);
                var registered = services.Last(d => d.ServiceType == typeof(IConversationStore));
                services.Remove(registered);
                services.AddSingleton<HookedConversationStore>(sp =>
                    new HookedConversationStore((IConversationStore)ActivatorUtilities.CreateInstance(sp, registered.ImplementationType!)));
                services.AddSingleton<IConversationStore>(sp => sp.GetRequiredService<HookedConversationStore>());
            });
            var host = base.CreateHost(builder);
            SeedWebChatChannel(host.Services, TenantValue);
            return host;
        }
    }

    /// <summary>A fresh database, the host over it, and the reads a test makes of the stores and of Postgres itself.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private readonly BoundHost _host;
        private readonly HttpClient _client;
        private readonly NpgsqlDataSource _db;
        private readonly List<ConversationStateChangedEvent> _stateChanges = [];
        private readonly IDisposable _subscription;

        private Env(string connectionString)
        {
            _host = new BoundHost(connectionString);
            _client = _host.CreateClient();
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {AuthenticatedPlatformApiFactory.TestApiKey}");
            _client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantValue);
            _db = NpgsqlDataSource.Create(connectionString);
            _subscription = Service<PlatformEventBus>().Events.Subscribe(e =>
            {
                if (e is ConversationStateChangedEvent changed)
                    lock (_stateChanges) _stateChanges.Add(changed);
            });
        }

        public HookedConversationStore Store => Service<HookedConversationStore>();
        public IContactStore Contacts => Service<IContactStore>();
        private IConversationStore Conversations => Service<IConversationStore>();
        private T Service<T>() where T : notnull => _host.Services.GetRequiredService<T>();

        public static async Task<Env> CreateAsync(ConversationClockReconcileLiveDbTests.PostgresContainer postgres)
        {
            var name = $"bind_{Guid.NewGuid():N}";
            await using (var admin = NpgsqlDataSource.Create(postgres.ConnectionStringFor("postgres")))
            await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
                await cmd.ExecuteNonQueryAsync();

            var env = new Env(postgres.ConnectionStringFor(name));
            await env.Service<IQueueStore>().SaveAsync(
                new Queue { QueueId = Queue, TenantId = Tenant, Name = "Session binding", CreatedAt = DateTimeOffset.UtcNow },
                CancellationToken.None);
            return env;
        }

        /// <summary>A session through the real route: its own contact and conversation.</summary>
        public async Task<WebChatSession> CreateSessionAsync()
        {
            using var response = await _client.PostAsJsonAsync("/api/v1/webchat/sessions", new { tenantId = TenantValue });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (await Service<WebChatSessionManager>().GetSessionAsync(body.RootElement.GetProperty("sessionId").GetString()!))!;
        }

        /// <summary>One contact holding two sessions, each with its conversation; the second one's conversation is the newer row.</summary>
        public async Task<(WebChatSession First, WebChatSession Second)> SeedContactWithTwoSessionsAsync()
        {
            var firstAddress = new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}");
            var secondAddress = new ChannelAddress(ChannelType.WebChat, $"webchat-{Guid.NewGuid():N}");
            var contact = new Contact { ContactId = EntityId.New(), TenantId = Tenant, CreatedAt = DateTimeOffset.UtcNow };
            contact.AddAddress(firstAddress);
            contact.AddAddress(secondAddress);
            await Contacts.SaveAsync(contact, CancellationToken.None);
            return (await SeedSessionAsync(contact.ContactId, firstAddress), await SeedSessionAsync(contact.ContactId, secondAddress));
        }

        private async Task<WebChatSession> SeedSessionAsync(EntityId contactId, ChannelAddress address)
        {
            var conversation = await Service<IConversationLifecycleService>()
                .CreateForWebChatSessionAsync(Tenant, contactId, address, CancellationToken.None);
            var sessions = Service<WebChatSessionManager>();
            var sessionId = await sessions.ConnectAsync(Tenant, contactId, conversation.ConversationId, address, Service<IClock>());
            return (await sessions.GetSessionAsync(sessionId))!;
        }

        public async Task PostAsync(WebChatSession session, string text)
        {
            using var send = await _client.PostAsJsonAsync($"/api/v1/webchat/sessions/{session.SessionId}/messages", new { text });
            send.StatusCode.Should().Be(HttpStatusCode.OK, await send.Content.ReadAsStringAsync());
        }

        public async Task DeleteContactThroughTheRouteAsync(EntityId contactId)
        {
            using var delete = await _client.DeleteAsync(new Uri($"/api/v1/contacts/{contactId.Value}", UriKind.Relative));
            delete.StatusCode.Should().Be(HttpStatusCode.NoContent, await delete.Content.ReadAsStringAsync());
        }

        public async Task<WebSocket> AttachSocketAsync(WebChatSession session, CancellationToken ct)
        {
            var socket = await _host.Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost/ws/webchat/{session.SessionId}"), ct);
            (await ReceiveTextAsync(socket, TimeSpan.FromSeconds(5))).Should().Contain("connected");
            return socket;
        }

        public Task<Message> ReplyAsync(EntityId conversationId, string text) =>
            Service<IConversationService>().SendMessageAsync(
                conversationId, Tenant, new MessageEnvelope([new TextBlock(text)]), Agent, ConversationOwnerKind.Agent, CancellationToken.None);

        public async Task<Conversation> ConversationAsync(EntityId conversationId) =>
            (await Conversations.GetByIdAsync(Tenant, conversationId, CancellationToken.None))!;

        public async Task GiveToAgentAsync(EntityId conversationId)
        {
            var conversation = await ConversationAsync(conversationId);
            conversation.State = ConversationState.Active;
            conversation.Owner = ConversationOwner.ForAgent(Agent);
            await Conversations.SaveAsync(conversation, CancellationToken.None);
        }

        public async Task ResolveAsync(EntityId conversationId)
        {
            var conversation = await ConversationAsync(conversationId);
            conversation.State = ConversationState.Resolved;
            conversation.Owner = ConversationOwner.ForAgent(Agent);
            await Conversations.SaveAsync(conversation, CancellationToken.None);
        }

        public async Task CloseAsync(EntityId conversationId)
        {
            var conversation = await ConversationAsync(conversationId);
            conversation.State = ConversationState.Closed;
            conversation.ClosedAt = DateTimeOffset.UtcNow;
            await Conversations.SaveAsync(conversation, CancellationToken.None);
        }

        /// <summary>Rewrites the contact holding only <paramref name="address"/>, directly in the store.</summary>
        public async Task ReplaceAddressesAsync(EntityId contactId, ChannelAddress address)
        {
            var held = await Contacts.GetByIdAsync(Tenant, contactId, CancellationToken.None);
            var rewritten = new Contact { ContactId = held!.ContactId, TenantId = held.TenantId, CreatedAt = held.CreatedAt };
            rewritten.AddAddress(address);
            await Contacts.SaveAsync(rewritten, CancellationToken.None);
        }

        /// <summary>The conversations holding an inbound message with <paramref name="text"/>, read from Postgres.</summary>
        public async Task<IReadOnlyList<string>> ConversationsHoldingAsync(string text)
        {
            await using var cmd = _db.CreateCommand(
                "SELECT conversation_id FROM messages WHERE tenant_id = @TenantId AND direction = @Direction AND content::text LIKE @Text");
            cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = TenantValue });
            cmd.Parameters.Add(new NpgsqlParameter("Direction", NpgsqlDbType.Integer) { Value = (int)MessageDirection.Inbound });
            cmd.Parameters.Add(new NpgsqlParameter("Text", NpgsqlDbType.Text) { Value = $"%{text}%" });
            var found = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                found.Add(reader.GetString(0));
            return found;
        }

        public async Task<IReadOnlyList<string>> WaitForConversationsHoldingAsync(string text)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while ((await ConversationsHoldingAsync(text)).Count == 0 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until the server-side socket loop stores the message
            return await ConversationsHoldingAsync(text);
        }

        public async Task<long> ContactCountAsync()
        {
            await using var cmd = _db.CreateCommand("SELECT count(*) FROM contacts WHERE tenant_id = @TenantId");
            cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = TenantValue });
            return (long)(await cmd.ExecuteScalarAsync())!;
        }

        public async Task<int> InboundCountAsync(EntityId conversationId) =>
            (await Service<IMessageStore>().GetConversationMessagesAsync(Tenant, conversationId, 100, 0, CancellationToken.None))
                .Count(m => m.Direction == MessageDirection.Inbound);

        /// <summary>Everything a message could change on a conversation, as one comparable string.</summary>
        public async Task<string> SignatureAsync(EntityId conversationId)
        {
            var c = await ConversationAsync(conversationId);
            var metadata = string.Join(',', c.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
            var inbound = await InboundCountAsync(conversationId);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{c.State}|{c.Owner?.Kind}|{c.Owner?.OwnerId}|{c.ContactId.Value}|{c.LastMessageAt:O}|{c.UpdatedAt:O}|{c.ClosedAt:O}|{metadata}|{inbound}");
        }

        public async Task<string> ContactSignatureAsync(EntityId contactId)
        {
            var c = await Contacts.GetByIdAsync(Tenant, contactId, CancellationToken.None);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{string.Join(',', c!.Addresses.Select(a => a.Channel + ":" + a.Address))}|{c.FirstName}|{c.LastName}|{c.UpdatedAt:O}|{c.UpdatedBy}");
        }

        public static void AssertAssignedToTheQueue(Conversation conversation)
        {
            conversation.Owner.Should().NotBeNull("the conversation is routed, so an agent is offered it");
            conversation.Owner!.Kind.Should().Be(ConversationOwnerKind.Queue);
            conversation.Owner.OwnerId.Should().Be(Queue);
        }

        public int ReturnCloseEventsOf(EntityId conversationId)
        {
            lock (_stateChanges)
            {
                return _stateChanges.Count(e => e.ConversationId == conversationId.Value
                    && e is { OldState: nameof(ConversationState.Resolved), NewState: nameof(ConversationState.Closed) });
            }
        }

        public int ReturnCloseAuditsOf(EntityId conversationId) =>
            _host.Audit.ReceivedCalls().Count(c =>
                c.GetMethodInfo().Name == nameof(IAuditService.RecordAsync)
                && c.GetArguments() is { Length: > 5 } args
                && args[2] is "conversation.closed"
                && args[4] is CustomerReturnAnnouncer.ActorId
                && args[6] is string target && target == conversationId.Value);

        /// <summary>The router's announcement of a routing, one for each conversation it routes.</summary>
        public int RoutingsOf(EntityId conversationId)
        {
            lock (_stateChanges)
                return _stateChanges.Count(e => e.ConversationId == conversationId.Value && e is { OldState: "", NewState: "Queued" });
        }

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            _client.Dispose();
            await _host.DisposeAsync();
            await _db.DisposeAsync();
        }
    }
}
