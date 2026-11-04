using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// contact-history-opens-by-assignment task 2.1 (design D1-D3, D16, D18; Platform/ADR-0047): the conversation read
/// rule on its full matrix of callers and conversations, through the real <see cref="PermissionAuthorizationHandler"/>
/// and the in-memory stores. The store half of "holds" is pinned on both stores by
/// <c>ConversationOwnershipStoreContract</c>.
/// </summary>
public sealed class ConversationReadRuleTests
{
    private static readonly TenantId Tenant = new("t-read-rule");
    private static readonly TenantId OtherTenant = new("t-read-rule-other");
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    // ── callers ─────────────────────────────────────────────────────────────────

    /// <summary>The callers of the matrix (task 2.1).</summary>
    public enum CallerKind
    {
        /// <summary>The Manager template: monitor, not in <c>SupervisorPlus</c>, no Agent profile.</summary>
        Monitor,

        /// <summary>The Quality Analyst template: read only, no Agent profile.</summary>
        Read,

        /// <summary>The Admin role claim, with no permission resolved.</summary>
        AdminRole,

        /// <summary>The Agent template with agent A's profile.</summary>
        HandleWithProfile,

        /// <summary>The Agent template with no profile.</summary>
        HandleWithoutProfile,

        /// <summary>None of handle, monitor and read, holding agent A's profile.</summary>
        NoneOfTheThree,

        /// <summary>An impersonation token minted with <c>read</c>.</summary>
        ImpersonationMintedRead,

        /// <summary>An impersonation token minted with <c>handle</c> only (its subject has no profile in the tenant).</summary>
        ImpersonationMintedHandle,

        /// <summary>An impersonation token with no conversation permission minted, despite its Admin role.</summary>
        ImpersonationNothingMinted,
    }

    private static bool ReadsAll(CallerKind caller) =>
        caller is CallerKind.Monitor or CallerKind.Read or CallerKind.AdminRole or CallerKind.ImpersonationMintedRead;

    private sealed class World
    {
        public InMemoryConversationStore Conversations { get; } = new();
        public InMemoryContactStore Contacts { get; } = new();
        public InMemoryAgentStore Agents { get; } = new();
        public IUserRoleStore Roles { get; } = Substitute.For<IUserRoleStore>();

        public EntityId AgentA { get; } = EntityId.From($"agent-a-{Guid.NewGuid():N}");
        public EntityId AgentB { get; } = EntityId.From($"agent-b-{Guid.NewGuid():N}");
        public EntityId AgentC { get; } = EntityId.From($"agent-c-{Guid.NewGuid():N}");
        public EntityId UserA { get; } = EntityId.From($"user-a-{Guid.NewGuid():N}");
        public EntityId QueueOne { get; } = EntityId.From($"queue-{Guid.NewGuid():N}");

        /// <summary>A per-sender contact.</summary>
        public Contact K { get; }

        /// <summary>The tenant's shared anonymous voice contact.</summary>
        public Contact S { get; }

        public World()
        {
            K = NewContact(Tenant, new ChannelAddress(ChannelType.WhatsApp, "+5491100000001"));
            S = NewContact(Tenant, SharedContactAddresses.AnonymousVoice);
            Contacts.SaveAsync(K, CancellationToken.None).GetAwaiter().GetResult();
            Contacts.SaveAsync(S, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var (agent, user) in new[] { (AgentA, UserA), (AgentB, EntityId.From("user-b")), (AgentC, EntityId.From("user-c")) })
            {
                Agents.SaveAsync(new Agent
                {
                    AgentId = agent,
                    TenantId = Tenant,
                    UserId = user,
                    DisplayName = agent.Value,
                    State = AgentState.Available,
                    CreatedAt = Now,
                }, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        public static Contact NewContact(TenantId tenant, ChannelAddress address)
        {
            var contact = new Contact { ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"), TenantId = tenant, CreatedAt = Now };
            contact.AddAddress(address);
            return contact;
        }

        public Conversation Save(Contact contact, ConversationOwner? owner, ConversationState state, string? offeredTo = null) =>
            Save(contact.TenantId, contact.ContactId, owner, state, offeredTo);

        public Conversation Save(TenantId tenant, EntityId contactId, ConversationOwner? owner, ConversationState state, string? offeredTo = null)
        {
            var conversation = new Conversation
            {
                ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
                TenantId = tenant,
                ContactId = contactId,
                Channel = ChannelType.WhatsApp,
                State = state,
                Owner = owner,
                CreatedAt = Now.AddHours(-1),
            };
            if (offeredTo is not null)
                conversation.SetMetadata("_offeredTo", offeredTo);
            Conversations.SaveAsync(conversation, CancellationToken.None).GetAwaiter().GetResult();
            return conversation;
        }

        public DefaultHttpContext Context(CallerKind caller)
        {
            var (claims, permissions, userId) = caller switch
            {
                CallerKind.Monitor => (User("user-manager", "Agent"), Template("manager"), "user-manager"),
                CallerKind.Read => (User("user-qa", "Agent"), Template("quality_analyst"), "user-qa"),
                CallerKind.AdminRole => (User("user-admin", "Admin"), [], "user-admin"),
                CallerKind.HandleWithProfile => (User(UserA.Value, "Agent"), Template("agent"), UserA.Value),
                CallerKind.HandleWithoutProfile => (User("user-no-profile", "Agent"), Template("agent"), "user-no-profile"),
                CallerKind.NoneOfTheThree => (User(UserA.Value, "Agent"), new[] { "contacts:contact:view" }, UserA.Value),
                CallerKind.ImpersonationMintedRead => (Impersonation(ConversationReaderClass.ReadPermission), [], "platform-admin"),
                CallerKind.ImpersonationMintedHandle => (Impersonation(ConversationReaderClass.HandlePermission), [], "platform-admin"),
                CallerKind.ImpersonationNothingMinted => (Impersonation("contacts:contact:view"), [], "platform-admin"),
                _ => throw new ArgumentOutOfRangeException(nameof(caller)),
            };
            Roles.GetEffectivePermissionsAsync(Tenant, EntityId.From(userId), Arg.Any<CancellationToken>())
                .Returns(new HashSet<string>(permissions, StringComparer.Ordinal));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthorization();
            services.AddSingleton(new PermissionResolver(Roles));
            services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
            return new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider(),
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            };
        }

        public Task<ConversationReader> ReaderAsync(CallerKind caller) =>
            ConversationReadRule.ResolveAsync(Context(caller), Tenant, Agents, Conversations, Contacts, CancellationToken.None);

        private static string[] Template(string templateId) => [.. RoleTemplateSeeder.GetTemplatePermissions(templateId)];

        private static Claim[] User(string userId, string role) =>
        [
            new("tenant_id", Tenant.Value),
            new("user_id", userId),
            new(ClaimTypes.Role, role),
        ];

        private static Claim[] Impersonation(string minted) =>
        [
            new("sub", "platform-admin"),
            new("tid", Tenant.Value),
            new("role", "Admin"),
            new("impersonation", "true"),
            new("impersonator_id", "platform-admin"),
            new("impersonator_tenant", "platform"),
            new("permissions", minted),
        ];
    }

    // ── the classification (design D2 steps 2-3) ────────────────────────────────

    [Theory]
    [InlineData(CallerKind.Monitor, true, true, false)] // the Manager template also grants handle; no profile
    [InlineData(CallerKind.Read, true, false, false)]
    [InlineData(CallerKind.AdminRole, true, true, false)]
    [InlineData(CallerKind.HandleWithProfile, false, true, true)]
    [InlineData(CallerKind.HandleWithoutProfile, false, true, false)]
    [InlineData(CallerKind.NoneOfTheThree, false, false, false)]
    [InlineData(CallerKind.ImpersonationMintedRead, true, false, false)]
    [InlineData(CallerKind.ImpersonationMintedHandle, false, true, false)]
    [InlineData(CallerKind.ImpersonationNothingMinted, false, false, false)]
    public async Task ResolveAsync_ShouldClassifyTheCaller_WhenItHoldsItsPermissionsAndProfile(
        CallerKind caller, bool readsAll, bool handles, bool readsAsAgentA)
    {
        var world = new World();

        var readerClass = await ConversationReaderClass.ResolveAsync(world.Context(caller), Tenant, world.Agents, CancellationToken.None);

        readerClass.ReadsAll.Should().Be(readsAll);
        readerClass.Handles.Should().Be(handles);
        readerClass.HandlingAgentId.Should().Be(readsAsAgentA ? world.AgentA : null);
    }

    [Fact]
    public void Permissions_ShouldBeCatalogued_ByPermissionSeeder()
    {
        PermissionCatalog.Contains(ConversationReaderClass.MonitorPermission).Should().BeTrue();
        PermissionCatalog.Contains(ConversationReaderClass.ReadPermission).Should().BeTrue();
        PermissionCatalog.Contains(ConversationReaderClass.HandlePermission).Should().BeTrue();
    }

    // ── CanReadAsync: the full matrix ───────────────────────────────────────────

    /// <summary>
    /// One conversation case of the matrix: it seeds the world (agent A's own conversations of the contact included)
    /// and returns the conversation read, plus whether agent A, a handle holder with a profile, may read it.
    /// </summary>
    private sealed record ConversationCase(string Name, Func<World, Conversation> Seed, bool AgentAReads, bool ReadersRead = true);

    private static readonly ConversationCase[] s_conversationCases =
    [
        .. Enum.GetValues<ConversationState>().Select(state => new ConversationCase(
            $"owned by A in {state}", w => w.Save(w.K, ConversationOwner.ForAgent(w.AgentA), state), AgentAReads: true)),
        new("offered to A", w => w.Save(w.K, ConversationOwner.ForQueue(w.QueueOne), ConversationState.Offered, w.AgentA.Value), true),
        new("offered to B", w => w.Save(w.K, ConversationOwner.ForQueue(w.QueueOne), ConversationState.Offered, w.AgentB.Value), false),
        .. new[] { ConversationState.Active, ConversationState.Resolved, ConversationState.WrapUp, ConversationState.WaitingForCustomer }
            .Select(state => new ConversationCase(
                $"B's closed one of K while A holds K in {state}",
                w =>
                {
                    w.Save(w.K, ConversationOwner.ForAgent(w.AgentA), state);
                    return w.Save(w.K, ConversationOwner.ForAgent(w.AgentB), ConversationState.Closed);
                },
                AgentAReads: true)),
        new("B's closed one of K while A is only offered one of K",
            w =>
            {
                w.Save(w.K, ConversationOwner.ForQueue(w.QueueOne), ConversationState.Offered, w.AgentA.Value);
                return w.Save(w.K, ConversationOwner.ForAgent(w.AgentB), ConversationState.Closed);
            },
            false),
        .. ConversationStateMachine.TerminalStates.Select(state => new ConversationCase(
            $"B's active one of K while A owns only a {state} one of K",
            w =>
            {
                w.Save(w.K, ConversationOwner.ForAgent(w.AgentA), state);
                return w.Save(w.K, ConversationOwner.ForAgent(w.AgentB), ConversationState.Active);
            },
            false)),
        new("B's closed one of K after A's WrapUp of K was closed by the wrap-up timeout",
            w =>
            {
                var wrapUp = w.Save(w.K, ConversationOwner.ForAgent(w.AgentA), ConversationState.WrapUp);
                wrapUp.TransitionTo(ConversationState.Closed, Now);
                w.Conversations.SaveAsync(wrapUp, CancellationToken.None).GetAwaiter().GetResult();
                return w.Save(w.K, ConversationOwner.ForAgent(w.AgentB), ConversationState.Closed);
            },
            false),
        new("A's conversation of K transferred away to C",
            w =>
            {
                var moved = w.Save(w.K, ConversationOwner.ForAgent(w.AgentA), ConversationState.Active);
                moved.Owner = ConversationOwner.ForAgent(w.AgentC);
                w.Conversations.SaveAsync(moved, CancellationToken.None).GetAwaiter().GetResult();
                return moved;
            },
            false),
        new("B's closed one of K after A transferred its only one of K to C",
            w =>
            {
                var moved = w.Save(w.K, ConversationOwner.ForAgent(w.AgentA), ConversationState.Active);
                moved.Owner = ConversationOwner.ForAgent(w.AgentC);
                w.Conversations.SaveAsync(moved, CancellationToken.None).GetAwaiter().GetResult();
                return w.Save(w.K, ConversationOwner.ForAgent(w.AgentB), ConversationState.Closed);
            },
            false),
        new("B's closed one of K while a queue sharing A's id owns one of K",
            w =>
            {
                w.Save(w.K, ConversationOwner.ForQueue(w.AgentA), ConversationState.Queued);
                return w.Save(w.K, ConversationOwner.ForAgent(w.AgentB), ConversationState.Closed);
            },
            false),
        new("B's closed call of the shared anonymous contact while A owns an active one",
            w =>
            {
                w.Save(w.S, ConversationOwner.ForAgent(w.AgentA), ConversationState.Active);
                return w.Save(w.S, ConversationOwner.ForAgent(w.AgentB), ConversationState.Closed);
            },
            false),
        new("A's own call of the shared anonymous contact",
            w => w.Save(w.S, ConversationOwner.ForAgent(w.AgentA), ConversationState.Active), true),
        new("A call of the shared anonymous contact offered to A",
            w => w.Save(w.S, ConversationOwner.ForQueue(w.QueueOne), ConversationState.Offered, w.AgentA.Value), true),
        new("B's closed one of a contact that no longer exists, while A holds one of it",
            w =>
            {
                var gone = EntityId.From($"contact-gone-{Guid.NewGuid():N}");
                w.Save(Tenant, gone, ConversationOwner.ForAgent(w.AgentA), ConversationState.Active);
                return w.Save(Tenant, gone, ConversationOwner.ForAgent(w.AgentB), ConversationState.Closed);
            },
            false),
        new("A conversation of another tenant owned by A's id",
            w => w.Save(OtherTenant, w.K.ContactId, ConversationOwner.ForAgent(w.AgentA), ConversationState.Active),
            false, ReadersRead: false),
    ];

    public static TheoryData<CallerKind, string> Matrix()
    {
        var data = new TheoryData<CallerKind, string>();
        foreach (var caller in Enum.GetValues<CallerKind>())
        {
            foreach (var conversationCase in s_conversationCases)
                data.Add(caller, conversationCase.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task CanReadAsync_ShouldDecideAsTheRuleSays_ForEveryCallerAndConversation(CallerKind caller, string caseName)
    {
        var conversationCase = s_conversationCases.Single(c => c.Name == caseName);
        var world = new World();
        var conversation = conversationCase.Seed(world);
        var reader = await world.ReaderAsync(caller);

        var expected = ReadsAll(caller)
            ? conversationCase.ReadersRead
            : caller == CallerKind.HandleWithProfile && conversationCase.AgentAReads;

        (await reader.CanReadAsync(conversation, CancellationToken.None)).Should().Be(expected,
            $"{caller} reading \"{conversationCase.Name}\" (design D2)");
    }

    // ── CanReadContactAsync and the shared contact's restriction (D5 step 2, D18) ──

    /// <summary>One contact case: it seeds the world and returns the contact, plus whether agent A may read its list.</summary>
    private sealed record ContactCase(string Name, Func<World, Contact> Seed, bool AgentAReads, bool ReadersRead = true);

    private static readonly ContactCase[] s_contactCases =
    [
        .. new[] { ConversationState.Active, ConversationState.Resolved, ConversationState.WrapUp, ConversationState.WaitingForCustomer }
            .Select(state => new ContactCase($"K while A holds one of it in {state}",
                w => { w.Save(w.K, ConversationOwner.ForAgent(w.AgentA), state); return w.K; }, true)),
        new("K while A is only offered one of it",
            w => { w.Save(w.K, ConversationOwner.ForQueue(w.QueueOne), ConversationState.Offered, w.AgentA.Value); return w.K; }, false),
        new("K while A owns only a closed one of it",
            w => { w.Save(w.K, ConversationOwner.ForAgent(w.AgentA), ConversationState.Closed); return w.K; }, false),
        new("K after A transferred its only one to C",
            w =>
            {
                var moved = w.Save(w.K, ConversationOwner.ForAgent(w.AgentA), ConversationState.Active);
                moved.Owner = ConversationOwner.ForAgent(w.AgentC);
                w.Conversations.SaveAsync(moved, CancellationToken.None).GetAwaiter().GetResult();
                return w.K;
            },
            false),
        new("K while B holds one of it and A nothing",
            w => { w.Save(w.K, ConversationOwner.ForAgent(w.AgentB), ConversationState.Active); return w.K; }, false),
        new("the shared contact while A owns an active call of it",
            w => { w.Save(w.S, ConversationOwner.ForAgent(w.AgentA), ConversationState.Active); return w.S; }, true),
        new("the shared contact while A owns only a closed call of it",
            w => { w.Save(w.S, ConversationOwner.ForAgent(w.AgentA), ConversationState.Closed); return w.S; }, false),
        new("the shared contact while A is only offered a call of it",
            w => { w.Save(w.S, ConversationOwner.ForQueue(w.QueueOne), ConversationState.Offered, w.AgentA.Value); return w.S; }, false),
        new("a contact of another tenant whose conversation A's id owns",
            w =>
            {
                var foreign = World.NewContact(OtherTenant, new ChannelAddress(ChannelType.WhatsApp, "+5491100000002"));
                w.Contacts.SaveAsync(foreign, CancellationToken.None).GetAwaiter().GetResult();
                w.Save(foreign, ConversationOwner.ForAgent(w.AgentA), ConversationState.Active);
                return foreign;
            },
            false, ReadersRead: false),
    ];

    public static TheoryData<CallerKind, string> ContactMatrix()
    {
        var data = new TheoryData<CallerKind, string>();
        foreach (var caller in Enum.GetValues<CallerKind>())
        {
            foreach (var contactCase in s_contactCases)
                data.Add(caller, contactCase.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ContactMatrix))]
    public async Task CanReadContactAsync_ShouldDecideAsTheRuleSays_ForEveryCallerAndContact(CallerKind caller, string caseName)
    {
        var contactCase = s_contactCases.Single(c => c.Name == caseName);
        var world = new World();
        var contact = contactCase.Seed(world);
        var reader = await world.ReaderAsync(caller);

        var expected = ReadsAll(caller)
            ? contactCase.ReadersRead
            : caller == CallerKind.HandleWithProfile && contactCase.AgentAReads;

        (await reader.CanReadContactAsync(contact, CancellationToken.None)).Should().Be(expected,
            $"{caller} reading the list of \"{contactCase.Name}\" (design D5 step 2, D18)");
    }

    [Theory]
    [InlineData(CallerKind.HandleWithProfile, true, true)]
    [InlineData(CallerKind.HandleWithProfile, false, false)]
    [InlineData(CallerKind.Monitor, true, false)]
    [InlineData(CallerKind.Read, true, false)]
    [InlineData(CallerKind.AdminRole, true, false)]
    public async Task OwnerRestrictionOf_ShouldNameTheAgent_OnlyWhenTheContactIsSharedAndTheCallerDoesNotReadAll(
        CallerKind caller, bool shared, bool restricted)
    {
        var world = new World();
        var reader = await world.ReaderAsync(caller);

        reader.OwnerRestrictionOf(shared ? world.S : world.K).Should().Be(restricted ? world.AgentA : null,
            "the list of a shared contact holds only the caller's own conversations, unless it reads everything (D5 step 3, D18)");
    }

    [Theory]
    [InlineData(CallerKind.HandleWithoutProfile)]
    [InlineData(CallerKind.NoneOfTheThree)]
    [InlineData(CallerKind.ImpersonationNothingMinted)]
    public async Task OwnerRestrictionOf_ShouldThrow_WhenTheContactIsSharedAndTheCallerHandlesNoProfile(CallerKind caller)
    {
        var world = new World();
        var reader = await world.ReaderAsync(caller);

        var act = () => reader.OwnerRestrictionOf(world.S);

        act.Should().Throw<InvalidOperationException>("an unrestricted list of the shared contact is never answered to a caller the rule refuses");
        reader.OwnerRestrictionOf(world.K).Should().BeNull("a per-sender contact's list is not restricted");
    }

    // Review round 1 (design D18): a voice contact a build before the fix resolved from Asterisk's "<unknown>" placeholder
    // (or a carrier's "Restricted") holds many withheld callers, as the anonymous contact does. The rule must treat it as
    // shared: holding one call of it opens neither another caller's call nor the unrestricted list.
    [Theory]
    [InlineData("<unknown>")]
    [InlineData("Restricted")]
    public async Task CanReadAsync_ShouldRefuseAnotherCallersCall_WhenTheVoiceContactIsAWithheldPlaceholder(string placeholder)
    {
        var world = new World();
        var legacy = World.NewContact(Tenant, new ChannelAddress(ChannelType.Voice, placeholder));
        await world.Contacts.SaveAsync(legacy, CancellationToken.None);
        world.Save(legacy, ConversationOwner.ForAgent(world.AgentA), ConversationState.Active);
        var others = world.Save(legacy, ConversationOwner.ForAgent(world.AgentB), ConversationState.Closed);
        var reader = await world.ReaderAsync(CallerKind.HandleWithProfile);

        (await reader.CanReadAsync(others, CancellationToken.None))
            .Should().BeFalse("a withheld-caller-id contact is shared, so holding one call of it opens no other caller's call (D18)");
        reader.OwnerRestrictionOf(legacy).Should().Be(world.AgentA,
            "the list of a shared contact holds only the caller's own conversations (D5 step 3, D18)");
    }

    // ── IsHeldByCaller (design D3) ───────────────────────────────────────────────

    [Theory]
    [InlineData(ConversationState.Active, true)]
    [InlineData(ConversationState.Resolved, true)]
    [InlineData(ConversationState.WrapUp, true)]
    [InlineData(ConversationState.WaitingForCustomer, true)]
    [InlineData(ConversationState.Closed, false)]
    [InlineData(ConversationState.Abandoned, false)]
    public async Task IsHeldByCaller_ShouldAnswerWhetherTheCallersProfileOwnsItOpen_WhenTheCallerOwnsIt(
        ConversationState state, bool held)
    {
        var world = new World();
        var reader = await world.ReaderAsync(CallerKind.HandleWithProfile);

        reader.IsHeldByCaller(world.Save(world.K, ConversationOwner.ForAgent(world.AgentA), state)).Should().Be(held);
    }

    [Fact]
    public async Task IsHeldByCaller_ShouldAnswerFalse_WhenTheRowIsOfferedOrAnotherAgentsOrTheCallerHasNoProfile()
    {
        var world = new World();
        var agentA = await world.ReaderAsync(CallerKind.HandleWithProfile);
        var supervisor = await world.ReaderAsync(CallerKind.Monitor);
        var owned = world.Save(world.K, ConversationOwner.ForAgent(world.AgentA), ConversationState.Active);

        agentA.IsHeldByCaller(world.Save(world.K, ConversationOwner.ForQueue(world.QueueOne), ConversationState.Offered, world.AgentA.Value))
            .Should().BeFalse("an offered row is not held (Q3)");
        agentA.IsHeldByCaller(world.Save(world.K, ConversationOwner.ForAgent(world.AgentB), ConversationState.Active))
            .Should().BeFalse("another agent's row");
        agentA.IsHeldByCaller(world.Save(world.K, ConversationOwner.ForQueue(world.AgentA), ConversationState.Queued))
            .Should().BeFalse("a queue sharing the agent's id owns nothing of the agent");
        supervisor.IsHeldByCaller(owned).Should().BeFalse("a caller with no Agent profile gets false everywhere");
    }

    // ── the evaluation order (design D2, D18 "Where the check runs") ───────────────

    private static (ConversationReader Reader, IConversationStore Conversations, IContactStore Contacts, World World) Probed(
        CallerKind caller)
    {
        var world = new World();
        var conversations = Substitute.For<IConversationStore>();
        var contacts = Substitute.For<IContactStore>();
        contacts.GetByIdAsync(Tenant, world.K.ContactId, Arg.Any<CancellationToken>()).Returns(world.K);
        contacts.GetByIdAsync(Tenant, world.S.ContactId, Arg.Any<CancellationToken>()).Returns(world.S);
        conversations.HoldsOpenConversationOfContactAsync(Tenant, Arg.Any<EntityId>(), world.AgentA, Arg.Any<CancellationToken>())
            .Returns(true);
        var reader = ConversationReadRule.ResolveAsync(world.Context(caller), Tenant, world.Agents, conversations, contacts, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (reader, conversations, contacts, world);
    }

    private static Conversation Unsaved(Contact contact, ConversationOwner owner, ConversationState state, string? offeredTo = null)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            ContactId = contact.ContactId,
            Channel = ChannelType.WhatsApp,
            State = state,
            Owner = owner,
            CreatedAt = Now,
        };
        if (offeredTo is not null)
            conversation.SetMetadata("_offeredTo", offeredTo);
        return conversation;
    }

    [Fact]
    public async Task CanReadAsync_ShouldNotLoadTheContactNorQueryHolding_WhenTheCallerOwnsOrIsOfferedTheConversation()
    {
        var (reader, conversations, contacts, world) = Probed(CallerKind.HandleWithProfile);

        (await reader.CanReadAsync(Unsaved(world.K, ConversationOwner.ForAgent(world.AgentA), ConversationState.Closed), CancellationToken.None))
            .Should().BeTrue();
        (await reader.CanReadAsync(
                Unsaved(world.K, ConversationOwner.ForQueue(world.QueueOne), ConversationState.Offered, world.AgentA.Value), CancellationToken.None))
            .Should().BeTrue();

        await contacts.DidNotReceiveWithAnyArgs().GetByIdAsync(default!, default!, default);
        await conversations.DidNotReceiveWithAnyArgs().HoldsOpenConversationOfContactAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task CanReadAsync_ShouldLoadTheContactOnceThenQueryHoldingOnce_WhenOnlyTheContactBranchCanAllow()
    {
        var (reader, conversations, contacts, world) = Probed(CallerKind.HandleWithProfile);

        (await reader.CanReadAsync(Unsaved(world.K, ConversationOwner.ForAgent(world.AgentB), ConversationState.Closed), CancellationToken.None))
            .Should().BeTrue();

        await contacts.Received(1).GetByIdAsync(Tenant, world.K.ContactId, Arg.Any<CancellationToken>());
        await conversations.Received(1).HoldsOpenConversationOfContactAsync(Tenant, world.K.ContactId, world.AgentA, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CanReadAsync_ShouldNotQueryHolding_WhenTheContactIsShared()
    {
        var (reader, conversations, _, world) = Probed(CallerKind.HandleWithProfile);

        (await reader.CanReadAsync(Unsaved(world.S, ConversationOwner.ForAgent(world.AgentB), ConversationState.Closed), CancellationToken.None))
            .Should().BeFalse("a shared contact never opens other callers' conversations (D18)");

        await conversations.DidNotReceiveWithAnyArgs().HoldsOpenConversationOfContactAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData(CallerKind.Monitor)]
    [InlineData(CallerKind.HandleWithoutProfile)]
    [InlineData(CallerKind.NoneOfTheThree)]
    public async Task CanReadAsync_ShouldTouchNoStore_WhenTheClassAloneDecides(CallerKind caller)
    {
        var (reader, conversations, contacts, world) = Probed(caller);

        await reader.CanReadAsync(Unsaved(world.K, ConversationOwner.ForAgent(world.AgentB), ConversationState.Closed), CancellationToken.None);
        await reader.CanReadContactAsync(world.K, CancellationToken.None);

        await contacts.DidNotReceiveWithAnyArgs().GetByIdAsync(default!, default!, default);
        await conversations.DidNotReceiveWithAnyArgs().HoldsOpenConversationOfContactAsync(default!, default!, default!, default);
    }

    [Fact]
    public void NotReadable_ShouldBeTheRefusalCodeOfTheFixture()
    {
        ConversationReadRule.NotReadable.Should().Be("not-readable");
        var refusal = ConversationReadRule.Refusal();
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        refusal.Value!.Error.Should().Be("not-readable");
    }
}
