using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Models;
using Verbara.Platform.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // ADR-0027 — the /admin/* surface is split in two sub-groups based on
        // semantic tenancy: NEUTRAL routes (users, RBAC-adjacent) act on the
        // caller's own tenant regardless of type and are valid on Platform,
        // Partner, and Customer tenants. OPERATIONAL routes (queues, agents,
        // teams, queue-members) act on tenant-internal data that only exists
        // inside a Customer tenant; calling them from Platform or Partner is
        // a semantic mistake that the gate converts to HTTP 409 with a
        // remediation hint pointing to POST /management/impersonate.
        var neutralGroup = app.MapGroup("/admin").RequireAuthorization("AdminOnly");
        var operationalGroup = app.MapGroup("/admin")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant();

        // Users — NEUTRAL (every tenant manages its own user directory)
        neutralGroup.MapGet("/users", ListUsers);
        neutralGroup.MapGet("/users/{id}", GetUser);
        neutralGroup.MapPost("/users", CreateUser);
        neutralGroup.MapPut("/users/{id}", UpdateUser);
        neutralGroup.MapDelete("/users/{id}", DeleteUser);

        // Queues — OPERATIONAL
        operationalGroup.MapGet("/queues", ListQueues);
        operationalGroup.MapGet("/queues/{id}", GetQueue);
        operationalGroup.MapPost("/queues", CreateQueue);
        operationalGroup.MapPut("/queues/{id}", UpdateQueue);
        operationalGroup.MapDelete("/queues/{id}", DeleteQueue);

        // Queue Members — legacy endpoints kept as 308 Permanent Redirects
        // to the RESTful nested routes under /queues/{queueId}/members.
        // Preserves backward-compat for clients pinned to /admin/queue-members.
        // OPERATIONAL (the redirect target is operational).
        operationalGroup.MapPost("/queue-members", RedirectAddQueueMember);
        operationalGroup.MapDelete("/queue-members/{queueId}/{agentId}", RedirectRemoveQueueMember);

        // Agents — OPERATIONAL
        operationalGroup.MapGet("/agents", ListAgents);
        operationalGroup.MapGet("/agents/{id}", GetAgent);
        operationalGroup.MapPost("/agents", CreateAgent);
        operationalGroup.MapPut("/agents/{id}", UpdateAgent);
        operationalGroup.MapDelete("/agents/{id}", DeleteAgent);
        // ADR-0026 Phase A.6 — agent-centric membership listing for the
        // /admin/agents/{agentId}/queues editor in the React admin UI.
        operationalGroup.MapGet("/agents/{id}/queue-memberships", ListAgentQueueMemberships);
        // W3 (A6) — manual supervisor lever to force a routing zombie / stuck
        // agent Offline (heartbeat/reaper detection is automatic; this is the
        // human escape hatch). Optionally revokes the agent's refresh-token
        // sessions so a wedged client cannot silently come back.
        operationalGroup.MapPost("/agents/{id}/force-offline", ForceAgentOffline);

        // Teams — OPERATIONAL
        operationalGroup.MapGet("/teams", ListTeams);
        operationalGroup.MapGet("/teams/{id}", GetTeam);
        operationalGroup.MapPost("/teams", CreateTeam);
        operationalGroup.MapPut("/teams/{id}", UpdateTeam);
        operationalGroup.MapDelete("/teams/{id}", DeleteTeam);
    }

    // ─── Users ────────────────────────────────────────────────────────────────

    private static async Task<Ok<PagedResult<UserDto>>> ListUsers(
        HttpContext context,
        [FromServices] IUserStore store,
        int page = 1,
        int pageSize = 25,
        // v1.14.3 (R5.5 P0 finding #5 fix). Pre-v1.14.3 the `email` query
        // string was silently dropped — the endpoint accepted it but never
        // forwarded it to the store. Now passes through to
        // IUserStore.ListAsync(..., email, ...) for case-insensitive
        // substring filtering.
        string? email = null,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, new PagedQuery { Page = page, PageSize = pageSize }, email, ct);
        var dtos = new PagedResult<UserDto>(
            result.Items.Select(ToUserDto).ToList(),
            result.TotalCount,
            result.Page,
            result.PageSize);
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Ok<UserDto>, NotFound>> GetUser(
        string id,
        HttpContext context,
        [FromServices] IUserStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var user = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return user is null ? TypedResults.NotFound() : TypedResults.Ok(ToUserDto(user));
    }

    private static async Task<IResult> CreateUser(
        HttpContext context,
        [FromBody] CreateUserRequest body,
        [FromServices] IUserStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var user = new User
        {
            UserId = EntityId.New(),
            TenantId = tenantId,
            Email = body.Email,
            DisplayName = body.DisplayName,
            Role = body.Role,
            Status = UserStatus.Active,
            PasswordHash = body.Password is not null ? PasswordService.HashPassword(body.Password) : null,
            CreatedAt = clock.UtcNow,
        };
        try
        {
            await store.SaveAsync(user, ct);
        }
        catch (EntityAlreadyExistsException ex)
        {
            // v1.14.3 (R5.5 P0 finding #4 fix). Pre-v1.14.3, a duplicate
            // email collided with `idx_users_email` UNIQUE → bubbled out as
            // PostgresException 23505 → 500 with the raw constraint name.
            // Now translates to RFC-7807 problem details with HTTP 409.
            return Results.Problem(
                title: "User already exists",
                detail: ex.ConflictingField is not null
                    ? $"A user with the supplied {ex.ConflictingField} already exists in this tenant."
                    : "A user with the supplied identifier already exists in this tenant.",
                statusCode: StatusCodes.Status409Conflict,
                type: "https://verbara.platform/errors/entity-already-exists");
        }
        return Results.Created($"/admin/users/{user.UserId}", ToUserDto(user));
    }

    private static async Task<Results<Ok<UserDto>, NotFound>> UpdateUser(
        string id,
        HttpContext context,
        [FromBody] UpdateUserRequest body,
        [FromServices] IUserStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var user = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (user is null)
            return TypedResults.NotFound();

        user.DisplayName = body.DisplayName ?? user.DisplayName;
        if (body.Role.HasValue) user.Role = body.Role.Value;
        if (body.Status.HasValue) user.Status = body.Status.Value;
        user.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(user, ct);
        return TypedResults.Ok(ToUserDto(user));
    }

    private static UserDto ToUserDto(User u) =>
        new(
            u.UserId.Value,
            u.Email,
            u.DisplayName,
            u.Role.ToString().ToLowerInvariant(),
            u.Status.ToString().ToLowerInvariant(),
            u.CreatedAt);

    private static async Task<IResult> DeleteUser(
        string id,
        HttpContext context,
        [FromServices] IUserStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        return Results.NoContent();
    }

    // ─── Queues ───────────────────────────────────────────────────────────────

    private static async Task<Ok<PagedResult<QueueDto>>> ListQueues(
        HttpContext context,
        [FromServices] IQueueStore store,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, new PagedQuery { Page = page, PageSize = pageSize }, ct);
        var dtos = new PagedResult<QueueDto>(
            result.Items.Select(ToQueueDto).ToList(),
            result.TotalCount,
            result.Page,
            result.PageSize);
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Ok<QueueDto>, NotFound>> GetQueue(
        string id,
        HttpContext context,
        [FromServices] IQueueStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return queue is null ? TypedResults.NotFound() : TypedResults.Ok(ToQueueDto(queue));
    }

    private static QueueDto ToQueueDto(Queue q) => new(
        q.QueueId.Value,
        q.Name,
        q.IsActive,
        q.MaxWaiting,
        q.SlaTargets,
        q.OverflowRule,
        q.WrapUp,
        q.RequiredSkills?.ToList() ?? [],
        q.AutoAnswerDefault,
        q.CreatedAt);

    private static async Task<IResult> CreateQueue(
        HttpContext context,
        [FromBody] CreateQueueRequest body,
        [FromServices] IQueueStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = new Queue
        {
            QueueId = EntityId.New(),
            TenantId = tenantId,
            Name = body.Name,
            IsActive = true,
            MaxWaiting = body.MaxWaiting,
            RequiredSkills = body.RequiredSkills ?? [],
            SlaTargets = body.SlaTargets is { } sla ? new SlaPolicyTarget
            {
                AnswerWithinSeconds = sla.AnswerWithinSeconds,
                FirstResponseWithinSeconds = sla.FirstResponseWithinSeconds,
                ResolutionWithinSeconds = sla.ResolutionWithinSeconds
            } : null,
            OverflowRule = body.OverflowRule is { } ovf ? new QueueOverflowRule
            {
                OverflowQueueId = EntityId.From(ovf.OverflowQueueId),
                OverflowAfterSeconds = ovf.OverflowAfterSeconds
            } : null,
            WrapUp = body.WrapUp is { } wu ? new WrapUpConfig
            {
                DefaultWrapUpSeconds = wu.DefaultWrapUpSeconds,
                ForceWrapUp = wu.ForceWrapUp
            } : new WrapUpConfig(),
            AutoAnswerDefault = body.AutoAnswerDefault ?? false,
            CreatedAt = clock.UtcNow,
        };
        try
        {
            await store.SaveAsync(queue, ct);
        }
        catch (EntityAlreadyExistsException ex)
        {
            // v1.14.3 (R5.5 P0 finding #4 fix). Defensive translation: any
            // future UNIQUE on (tenant_id, name) — or any other constraint
            // the operator might add — surfaces as a 409 instead of leaking
            // the raw Postgres constraint name in a 500 body.
            return Results.Problem(
                title: "Queue already exists",
                detail: ex.ConflictingField is not null
                    ? $"A queue with the supplied {ex.ConflictingField} already exists in this tenant."
                    : "A queue with the supplied identifier already exists in this tenant.",
                statusCode: StatusCodes.Status409Conflict,
                type: "https://verbara.platform/errors/entity-already-exists");
        }

        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null)
        {
            try
            {
                var opts = new RealtimeQueueOptions
                {
                    Timeout = 30,
                    Wrapuptime = queue.WrapUp?.DefaultWrapUpSeconds ?? 15,
                    Servicelevel = queue.SlaTargets?.AnswerWithinSeconds ?? 20,
                    Maxlen = queue.MaxWaiting ?? 0,
                };
                await syncService.SyncQueueAsync(tenantId, queue.Name, opts, ct);
            }
            catch { }
        }

        return Results.Created($"/admin/queues/{queue.QueueId}", ToQueueDto(queue));
    }

    private static async Task<Results<Ok<QueueDto>, NotFound>> UpdateQueue(
        string id,
        HttpContext context,
        [FromBody] UpdateQueueRequest body,
        [FromServices] IQueueStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (queue is null)
            return TypedResults.NotFound();

        if (body.Name is not null) queue.Name = body.Name;
        if (body.IsActive.HasValue) queue.IsActive = body.IsActive.Value;
        if (body.MaxWaiting.HasValue) queue.MaxWaiting = body.MaxWaiting;
        if (body.RequiredSkills is not null) queue.RequiredSkills = body.RequiredSkills;
        if (body.SlaTargets is { } sla)
            queue.SlaTargets = new SlaPolicyTarget
            {
                AnswerWithinSeconds = sla.AnswerWithinSeconds,
                FirstResponseWithinSeconds = sla.FirstResponseWithinSeconds,
                ResolutionWithinSeconds = sla.ResolutionWithinSeconds
            };
        if (body.OverflowRule is { } ovf)
            queue.OverflowRule = new QueueOverflowRule
            {
                OverflowQueueId = EntityId.From(ovf.OverflowQueueId),
                OverflowAfterSeconds = ovf.OverflowAfterSeconds
            };
        if (body.WrapUp is { } wu)
            queue.WrapUp = new WrapUpConfig
            {
                DefaultWrapUpSeconds = wu.DefaultWrapUpSeconds,
                ForceWrapUp = wu.ForceWrapUp
            };
        if (body.AutoAnswerDefault.HasValue) queue.AutoAnswerDefault = body.AutoAnswerDefault.Value;
        queue.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(queue, ct);

        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null)
        {
            try
            {
                var opts = new RealtimeQueueOptions
                {
                    Timeout = 30,
                    Wrapuptime = queue.WrapUp?.DefaultWrapUpSeconds ?? 15,
                    Servicelevel = queue.SlaTargets?.AnswerWithinSeconds ?? 20,
                    Maxlen = queue.MaxWaiting ?? 0,
                };
                await syncService.SyncQueueAsync(tenantId, queue.Name, opts, ct);
            }
            catch { }
        }

        return TypedResults.Ok(ToQueueDto(queue));
    }

    private static async Task<IResult> DeleteQueue(
        string id,
        HttpContext context,
        [FromServices] IQueueStore store,
        [FromServices] IQueueMembershipStore membershipStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (queue is not null)
        {
            var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
            if (syncService is not null)
            {
                try { await syncService.RemoveQueueAsync(tenantId, queue.Name, ct); }
                catch { }
            }
        }

        await membershipStore.DeleteAllForQueueAsync(tenantId, EntityId.From(id), ct);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        return Results.NoContent();
    }

    // ─── Queue Members ────────────────────────────────────────────────────────
    //
    // The legacy POST/DELETE /admin/queue-members routes are kept as 308 Permanent
    // Redirects. The canonical RESTful endpoints live in QueueMembersEndpoints.cs
    // under /queues/{queueId}/members. Body semantics are preserved; clients that
    // follow redirects (curl -L, HttpClient default) get transparent forwarding.

    private static IResult RedirectAddQueueMember(
        HttpContext context, [FromBody] AddQueueMemberRequest body)
    {
        // Route is authenticated (AdminOnly). We have read the body to ensure the
        // model binder validates it before issuing the redirect, so misrouted bad
        // requests fail at the redirect target and not in some intermediate proxy.
        var target = $"/api/v1/queues/{Uri.EscapeDataString(body.QueueId)}/members";
        return Results.Redirect(target, permanent: true, preserveMethod: true);
    }

    private static IResult RedirectRemoveQueueMember(string queueId, string agentId, HttpContext context)
    {
        var target = $"/api/v1/queues/{Uri.EscapeDataString(queueId)}/members/{Uri.EscapeDataString(agentId)}";
        return Results.Redirect(target, permanent: true, preserveMethod: true);
    }

    // ─── Agents ───────────────────────────────────────────────────────────────

    private static async Task<Ok<PagedResult<AdminAgentResponseDto>>> ListAgents(
        HttpContext context,
        [FromServices] IAgentStore store,
        [FromServices] ICapacityDefaultsProvider defaultsProvider,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, new AgentQuery { Page = page, PageSize = pageSize }, ct);

        // W6-A6 — fetch the tenant defaults ONCE, then resolve each already-loaded agent's
        // effective capacity in-memory (AgentCapacityResolver.ResolveEffective) instead of calling
        // the resolver per agent, which would re-read each agent from the store (N+1).
        var defaults = await defaultsProvider.GetDefaultsAsync(tenantId, ct);
        var items = result.Items
            .Select(a => AdminAgentResponseDto.FromAgent(a, AgentCapacityResolver.ResolveEffective(a.CapacityOverride, defaults)))
            .ToList();
        var dtos = new PagedResult<AdminAgentResponseDto>(items, result.TotalCount, result.Page, result.PageSize);
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Ok<AdminAgentResponseDto>, NotFound>> GetAgent(
        string id,
        HttpContext context,
        [FromServices] IAgentStore store,
        [FromServices] ICapacityDefaultsProvider defaultsProvider,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agent = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (agent is null) return TypedResults.NotFound();

        var defaults = await defaultsProvider.GetDefaultsAsync(tenantId, ct);
        var effective = AgentCapacityResolver.ResolveEffective(agent.CapacityOverride, defaults);
        return TypedResults.Ok(AdminAgentResponseDto.FromAgent(agent, effective));
    }

    private static async Task<IResult> CreateAgent(
        HttpContext context,
        [FromBody] CreateAgentRequest body,
        [FromServices] IAgentStore store,
        [FromServices] IQueueStore queueStore,
        [FromServices] IQueueMembershipStore membershipStore,
        [FromServices] ICapacityDefaultsProvider defaultsProvider,
        [FromServices] IAuditService audit,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        // W6-A6 — validate the optional capacity override before any write.
        if (body.Capacity is { } cap && ValidateCapacity(cap) is { } capError)
            return Results.BadRequest(capError);

        // ADR-0026 Phase A.1 validation: normalize + verify allowed_channels.
        // Per-membership: AllowedChannels=null means "all channels the queue accepts";
        // an empty array is rejected — operator should use IsExcluded=true instead
        // for audit clarity. Each non-null channel must match the ChannelType enum.
        if (body.QueueMemberships is { Count: > 0 } pendingMemberships)
        {
            foreach (var m in pendingMemberships)
            {
                if (m.AllowedChannels is { Count: 0 })
                    return Results.BadRequest($"AllowedChannels must be null (= all channels) or contain at least one channel. Empty arrays are not allowed; use IsExcluded=true for that semantic.");
                if (m.AllowedChannels is { } channels)
                {
                    foreach (var ch in channels)
                    {
                        if (!Enum.TryParse<ChannelType>(ch, ignoreCase: true, out _))
                            return Results.BadRequest($"Unknown channel: '{ch}'. Allowed values match ChannelType enum (Voice, WhatsApp, Sms, WebChat, Email, Messenger, Instagram, Telegram, Twitter, Video, Rcs).");
                    }
                }
            }
        }

        var agent = new Agent
        {
            AgentId = EntityId.New(),
            TenantId = tenantId,
            UserId = EntityId.From(body.UserId),
            DisplayName = body.DisplayName,
            State = AgentState.Offline,
            CreatedAt = clock.UtcNow,
        };
        if (body.Extension is not null) agent.Extension = body.Extension;
        if (body.SipPassword is not null) agent.SipPassword = body.SipPassword;
        if (body.AutoAnswer is not null) agent.AutoAnswer = body.AutoAnswer;
        if (body.Capacity is { } capOverride) agent.CapacityOverride = ToOverride(capOverride);
        await store.SaveAsync(agent, ct);

        // W6-A6/M1 — best-effort audit when an override was supplied at creation (no old value).
        // Skip when the supplied override is itself all-null (== empty): that sets no real override,
        // so recording an old==new entry would be noise (consistent with the update-path check).
        var emptyOverride = new ChannelCapacityOverride();
        if (body.Capacity is not null && CapacityOverrideChanged(emptyOverride, agent.CapacityOverride))
            await RecordCapacityAuditAsync(audit, tenantId, GetCurrentUserId(context), agent.AgentId,
                oldOverride: emptyOverride, newOverride: agent.CapacityOverride, ct);

        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (!string.IsNullOrEmpty(agent.Extension) && !string.IsNullOrEmpty(agent.SipPassword) && syncService is not null)
        {
            try { await syncService.SyncAgentAsync(tenantId, agent.AgentId.Value, agent.DisplayName, agent.Extension, agent.SipPassword, ct: ct); }
            catch { }
        }

        // ADR-0026 Phase A.1 — associate agent to queues with channel-aware
        // memberships. Sync to Asterisk queue_members is conditional: voice
        // is included by default (AllowedChannels=null) or explicitly listed.
        if (body.QueueMemberships is { Count: > 0 } memberships)
        {
            foreach (var m in memberships)
            {
                var queue = await queueStore.GetByIdAsync(tenantId, EntityId.From(m.QueueId), ct);
                if (queue is null) continue;

                var penalty = Math.Clamp(m.Penalty ?? 0, 0, 10);
                await membershipStore.SaveAsync(new QueueMembership
                {
                    TenantId = tenantId,
                    QueueId = queue.QueueId,
                    AgentId = agent.AgentId,
                    Penalty = penalty,
                    Source = MembershipSource.Manual,
                    IsExcluded = false,
                    CreatedAt = clock.UtcNow,
                    AllowedChannels = m.AllowedChannels,
                }, ct);

                // ADR-0026 Phase B (SDK Pro v2.6.0-pro) — voice-gate moved
                // into IRealtimeSyncService.AddQueueMemberAsync. Just pass
                // AllowedChannels through; the SDK upserts when null/voice
                // included, or short-circuits to RemoveQueueMember otherwise.
                if (syncService is not null)
                {
                    try
                    {
                        await syncService.AddQueueMemberAsync(
                            tenantId.Value, queue.Name, agent.AgentId.Value,
                            agent.DisplayName, penalty,
                            allowedChannels: m.AllowedChannels, ct);
                    }
                    catch { }
                }
            }
        }

        var defaults = await defaultsProvider.GetDefaultsAsync(tenantId, ct);
        var effective = AgentCapacityResolver.ResolveEffective(agent.CapacityOverride, defaults);
        return Results.Created($"/admin/agents/{agent.AgentId}", AdminAgentResponseDto.FromAgent(agent, effective));
    }

    private static async Task<Results<Ok<AdminAgentResponseDto>, BadRequest<string>, NotFound>> UpdateAgent(
        string id,
        HttpContext context,
        [FromBody] UpdateAgentRequest body,
        [FromServices] IAgentStore store,
        [FromServices] ICapacityDefaultsProvider defaultsProvider,
        [FromServices] IAuditService audit,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        // W6-A6 — validate the optional capacity override before loading/mutating the agent.
        if (body.Capacity is { } cap && ValidateCapacity(cap) is { } capError)
            return TypedResults.BadRequest(capError);

        var agent = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (agent is null)
            return TypedResults.NotFound();

        if (body.DisplayName is not null) agent.DisplayName = body.DisplayName;
        if (body.TeamId is not null) agent.TeamId = EntityId.From(body.TeamId);
        if (body.Skills is not null) agent.Skills = body.Skills;
        if (body.Extension is not null) agent.Extension = body.Extension;
        if (body.SipPassword is not null) agent.SipPassword = body.SipPassword;
        // Auto-answer is tri-state: null is a MEANINGFUL value (inherit the queue default), not
        // "field absent". Set it unconditionally so the admin UI can reset On/Off back to Inherit —
        // the only writer is the always-complete agent form, and inherit falls back to the queue
        // default (false by default = manual), so an omitting caller's reset is benign.
        agent.AutoAnswer = body.AutoAnswer;

        // W6-A6 — capacity override: null leaves the existing override untouched (like other
        // optional fields); a populated DTO replaces it wholesale. Snapshot the old value first
        // so the audit records the before/after.
        var oldOverride = agent.CapacityOverride;
        var capacityChanged = body.Capacity is not null;
        if (body.Capacity is { } newCap) agent.CapacityOverride = ToOverride(newCap);

        agent.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(agent, ct);

        // W6-M1 — only audit when an override was supplied AND it actually differs from the old
        // value (no-op overrides — e.g. re-submitting the same form — must not record old==new).
        if (capacityChanged && CapacityOverrideChanged(oldOverride, agent.CapacityOverride))
            await RecordCapacityAuditAsync(audit, tenantId, GetCurrentUserId(context), agent.AgentId,
                oldOverride, agent.CapacityOverride, ct);

        if (!string.IsNullOrEmpty(agent.Extension) && !string.IsNullOrEmpty(agent.SipPassword))
        {
            var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
            if (syncService is not null)
            {
                try { await syncService.SyncAgentAsync(tenantId, agent.AgentId.Value, agent.DisplayName, agent.Extension, agent.SipPassword, ct: ct); }
                catch { }
            }
        }

        var defaults = await defaultsProvider.GetDefaultsAsync(tenantId, ct);
        var effective = AgentCapacityResolver.ResolveEffective(agent.CapacityOverride, defaults);
        return TypedResults.Ok(AdminAgentResponseDto.FromAgent(agent, effective));
    }

    private static async Task<IResult> DeleteAgent(
        string id,
        HttpContext context,
        [FromServices] IAgentStore store,
        [FromServices] IQueueMembershipStore membershipStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agent = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (agent is null) return Results.NotFound();

        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null)
        {
            try { await syncService.RemoveAgentAsync(tenantId, agent.AgentId.Value, ct); }
            catch { }
        }

        await membershipStore.DeleteAllForAgentAsync(tenantId, EntityId.From(id), ct);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        return Results.NoContent();
    }

    // ADR-0026 Phase A.6 — agent-centric membership listing used by the
    // /admin/agents/{agentId}/queues editor. Joins queue_memberships with
    // queues to project queue names alongside membership details so the
    // React UI can render channel multi-selects without an N+1 fetch.
    private static async Task<Results<Ok<List<AgentQueueMembershipDto>>, NotFound>> ListAgentQueueMemberships(
        string id,
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IQueueStore queueStore,
        [FromServices] IQueueMembershipStore membershipStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agent = await agentStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (agent is null) return TypedResults.NotFound();

        var memberships = await membershipStore.ListByAgentAsync(tenantId, agent.AgentId, ct);
        var result = new List<AgentQueueMembershipDto>(memberships.Count);
        foreach (var m in memberships)
        {
            var queue = await queueStore.GetByIdAsync(tenantId, m.QueueId, ct);
            if (queue is null) continue;
            result.Add(new AgentQueueMembershipDto(
                m.QueueId.Value,
                queue.Name,
                m.Penalty,
                m.IsExcluded,
                m.AllowedChannels,
                m.Source == MembershipSource.Manual ? "Manual" : "Skill"));
        }
        return TypedResults.Ok(result);
    }

    // W3 (A6) — admin force-offline. The MANUAL counterpart to the automatic
    // heartbeat/reaper liveness flow: a supervisor kicks a routing zombie /
    // wedged agent Offline from the UI. Mirrors AgentEndpoints.GoOffline
    // (ForceOffline + RemoveAsync + conditional-publish) plus an optional
    // refresh-token session revoke so a stuck client cannot silently re-appear.
    // Idempotent: an already-Offline agent still 204s, still removes the
    // presence key, still revokes sessions when asked — it just publishes no
    // duplicate AgentStateChangedEvent.
    private static async Task<IResult> ForceAgentOffline(
        string id,
        [FromBody] ForceAgentOfflineRequest body,
        HttpContext context,
        [FromServices] IAgentStore agentStore,
        [FromServices] IAgentLivenessStore livenessStore,
        [FromServices] IRefreshTokenStore refreshTokenStore,
        PlatformEventBus eventBus,
        [FromServices] IAuditService audit,
        [FromServices] TimeProvider clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var callerUserId = GetCurrentUserId(context);

        var agent = await agentStore.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (agent is null)
            return Results.NotFound();

        var oldState = agent.State;
        agent.ForceOffline(clock.GetUtcNow());   // W5 — deterministic grace stamp
        await agentStore.SaveAsync(agent, ct);
        await livenessStore.RemoveAsync(tenantId, agent.AgentId, ct);

        // Publish ONLY on a real transition so a force-offline against an
        // already-Offline agent doesn't spam RealtimeStateBridge → AMI QueuePause.
        if (oldState != AgentState.Offline)
            eventBus.Publish(new AgentStateChangedEvent(
                tenantId.ToString(),
                agent.AgentId.Value,
                agent.DisplayName,
                oldState.ToString(),
                AgentState.Offline.ToString()));

        // Optional hard session revoke. RevokeAllForUserAsync expects the USER
        // id (not the agent id) — agent.UserId is the owning user.
        if (body.RevokeSessions)
            await refreshTokenStore.RevokeAllForUserAsync(
                tenantId.ToString(), agent.UserId.Value, clock.GetUtcNow(), ct);

        // Best-effort audit (mirrors the reaper: a failed audit write must not
        // fail the operator's force-offline). Same category ("queues") as the
        // automatic liveness path so both surface together in the audit log.
        try
        {
            await audit.RecordAsync(
                tenantId,
                category: "queues",
                action: "agent.force_offline",
                severity: "warning",
                actorId: callerUserId.Value,
                actorType: "user",
                targetId: agent.AgentId.Value,
                targetType: "Agent",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["old_state"] = oldState.ToString(),
                    ["revoked_sessions"] = body.RevokeSessions ? "true" : "false",
                },
                ct: ct);
        }
        catch
        {
            // Swallow — the force-offline already succeeded; audit is advisory.
        }

        return Results.NoContent();
    }

    // ─── Teams ────────────────────────────────────────────────────────────────

    private static async Task<Ok<PagedResult<TeamDto>>> ListTeams(
        HttpContext context,
        [FromServices] ITeamStore store,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, new PagedQuery { Page = page, PageSize = pageSize }, ct);
        var dtos = new PagedResult<TeamDto>(
            result.Items.Select(ToDto).ToList(),
            result.TotalCount,
            result.Page,
            result.PageSize);
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Ok<TeamDto>, NotFound>> GetTeam(
        string id,
        HttpContext context,
        [FromServices] ITeamStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var team = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return team is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(team));
    }

    private static async Task<IResult> CreateTeam(
        HttpContext context,
        [FromBody] CreateTeamRequest body,
        [FromServices] ITeamStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var team = new Team
        {
            TeamId = EntityId.New(),
            TenantId = tenantId,
            Name = body.Name,
            CreatedAt = clock.UtcNow,
        };
        await store.SaveAsync(team, ct);
        return Results.Created($"/admin/teams/{team.TeamId}", ToDto(team));
    }

    private static async Task<Results<Ok<TeamDto>, NotFound>> UpdateTeam(
        string id,
        HttpContext context,
        [FromBody] UpdateTeamRequest body,
        [FromServices] ITeamStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var team = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (team is null)
            return TypedResults.NotFound();

        if (body.Name is not null) team.Name = body.Name;
        team.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(team, ct);
        return TypedResults.Ok(ToDto(team));
    }

    private static TeamDto ToDto(Team t) =>
        new(t.TeamId.Value, t.Name, 0, t.CreatedAt);

    private static async Task<IResult> DeleteTeam(
        string id,
        HttpContext context,
        [FromServices] ITeamStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        await store.DeleteAsync(tenantId, EntityId.From(id), ct);
        return Results.NoContent();
    }

    // ─── W6-A6 capacity helpers ─────────────────────────────────────────────────

    // The async-channel caps (chat/email/sms/total) accept 0..50: 0 disables the
    // channel for the agent, 50 is an absurd-but-safe upper bound that rejects typos
    // and negatives. Voice is pinned to a single exclusive lane (W5b ARI mixing-bridge
    // deferred), so MaxVoice may only be null (inherit) or exactly 1.
    private const int MaxCapacityValue = 50;

    /// <summary>
    /// W6-A6 — validates a per-agent capacity override. Returns a human-readable error string for
    /// HTTP 400 (Results.BadRequest, matching the sibling agent-create channel validation), or null
    /// when valid. MaxVoice must be null or 1; each other field, when non-null, must be in [0, 50].
    /// </summary>
    private static string? ValidateCapacity(ChannelCapacityOverrideDto cap)
    {
        if (cap.MaxVoice is { } v && v != 1)
            return "MaxVoice must be null (inherit) or 1. Concurrent voice is a single exclusive lane until the ARI mixing-bridge lands.";

        if (RangeError(cap.MaxChat, nameof(cap.MaxChat)) is { } chatErr) return chatErr;
        if (RangeError(cap.MaxEmail, nameof(cap.MaxEmail)) is { } emailErr) return emailErr;
        if (RangeError(cap.MaxSms, nameof(cap.MaxSms)) is { } smsErr) return smsErr;
        if (RangeError(cap.MaxTotal, nameof(cap.MaxTotal)) is { } totalErr) return totalErr;
        return null;

        static string? RangeError(int? value, string field) =>
            value is { } x && (x < 0 || x > MaxCapacityValue)
                ? $"{field} must be null (inherit) or between 0 and {MaxCapacityValue}."
                : null;
    }

    private static ChannelCapacityOverride ToOverride(ChannelCapacityOverrideDto cap) => new()
    {
        MaxVoice = cap.MaxVoice,
        MaxChat = cap.MaxChat,
        MaxEmail = cap.MaxEmail,
        MaxSms = cap.MaxSms,
        MaxTotal = cap.MaxTotal,
    };

    // W6-M1 — value-equality for the capacity override so the audit only fires on a REAL change
    // (mirrors A7's tenant-default "changed" check). ChannelCapacityOverride is a class, NOT a
    // record, so the default `==` is reference equality — compare the 5 nullable ints field-by-field.
    private static bool CapacityOverrideChanged(ChannelCapacityOverride old, ChannelCapacityOverride @new) =>
        old.MaxVoice != @new.MaxVoice
        || old.MaxChat != @new.MaxChat
        || old.MaxEmail != @new.MaxEmail
        || old.MaxSms != @new.MaxSms
        || old.MaxTotal != @new.MaxTotal;

    // W6-A6 — best-effort capacity audit (mirrors ForceAgentOffline: a failed audit write must
    // NEVER fail the operator's request). Same category ("queues") as the other agent-lifecycle
    // audit entries so capacity changes surface alongside force-offline + state transitions.
    private static async Task RecordCapacityAuditAsync(
        IAuditService audit,
        TenantId tenantId,
        EntityId actorId,
        EntityId agentId,
        ChannelCapacityOverride oldOverride,
        ChannelCapacityOverride newOverride,
        CancellationToken ct)
    {
        try
        {
            await audit.RecordAsync(
                tenantId,
                category: "queues",
                action: "agent.capacity_override",
                severity: "info",
                actorId: actorId.Value,
                actorType: "user",
                targetId: agentId.Value,
                targetType: "Agent",
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["old_max_voice"] = FormatNullable(oldOverride.MaxVoice),
                    ["old_max_chat"] = FormatNullable(oldOverride.MaxChat),
                    ["old_max_email"] = FormatNullable(oldOverride.MaxEmail),
                    ["old_max_sms"] = FormatNullable(oldOverride.MaxSms),
                    ["old_max_total"] = FormatNullable(oldOverride.MaxTotal),
                    ["new_max_voice"] = FormatNullable(newOverride.MaxVoice),
                    ["new_max_chat"] = FormatNullable(newOverride.MaxChat),
                    ["new_max_email"] = FormatNullable(newOverride.MaxEmail),
                    ["new_max_sms"] = FormatNullable(newOverride.MaxSms),
                    ["new_max_total"] = FormatNullable(newOverride.MaxTotal),
                },
                ct: ct);
        }
        catch
        {
            // Swallow — the capacity write already succeeded; audit is advisory.
        }

        static string FormatNullable(int? value) =>
            value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "inherit";
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }

    // Resolves the CALLER's user id from claims. Mirrors AgentEndpoints +
    // PermissionAuthorizationHandler: with MapInboundClaims=false the JWT `sub`
    // is the primary source; API-key auth links the user via the `user_id`
    // claim; NameIdentifier is the last-resort fallback (and is the KEY id for
    // API-key callers, hence checked last). Used as the audit actor id.
    private static EntityId GetCurrentUserId(HttpContext context)
    {
        var nameId = context.User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst("user_id")?.Value
            ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return nameId is not null ? EntityId.From(nameId) : EntityId.New();
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record CreateUserRequest(string Email, string DisplayName, UserRole Role, string? Password = null);
internal sealed record UpdateUserRequest(string? DisplayName, UserRole? Role, UserStatus? Status);

internal sealed record CreateQueueRequest(
    string Name,
    SlaPolicyTargetDto? SlaTargets = null,
    QueueOverflowRuleDto? OverflowRule = null,
    WrapUpConfigDto? WrapUp = null,
    int? MaxWaiting = null,
    IReadOnlyList<string>? RequiredSkills = null,
    bool? AutoAnswerDefault = null);

internal sealed record UpdateQueueRequest(
    string? Name = null,
    bool? IsActive = null,
    SlaPolicyTargetDto? SlaTargets = null,
    QueueOverflowRuleDto? OverflowRule = null,
    WrapUpConfigDto? WrapUp = null,
    int? MaxWaiting = null,
    IReadOnlyList<string>? RequiredSkills = null,
    bool? AutoAnswerDefault = null);

internal sealed record SlaPolicyTargetDto(
    int? AnswerWithinSeconds = null,
    int? FirstResponseWithinSeconds = null,
    int? ResolutionWithinSeconds = null);

internal sealed record QueueOverflowRuleDto(
    string OverflowQueueId,
    int OverflowAfterSeconds);

internal sealed record WrapUpConfigDto(
    int DefaultWrapUpSeconds = 30,
    bool ForceWrapUp = false);

internal sealed record AddQueueMemberRequest(string QueueId, string AgentId, int? Penalty = null);

internal sealed record CreateAgentRequest(
    string UserId,
    string DisplayName,
    string? Extension = null,
    string? SipPassword = null,
    bool? AutoAnswer = null,
    // W6-A6 — optional per-agent capacity override. null = inherit the tenant default
    // for every channel; a populated DTO sets only its non-null fields.
    ChannelCapacityOverrideDto? Capacity = null,
    IReadOnlyList<QueueMembershipRequest>? QueueMemberships = null);

/// <summary>
/// W6-A6 — wire shape for the per-agent <see cref="ChannelCapacityOverride"/>. Each null field
/// means "inherit the tenant default" for that channel; a non-null value overrides it. Returned
/// on the admin agent representation as <c>capacityOverride</c> (null when fully inherited) and
/// accepted on create/update as <c>capacity</c>.
/// </summary>
internal sealed record ChannelCapacityOverrideDto(
    int? MaxVoice,
    int? MaxChat,
    int? MaxEmail,
    int? MaxSms,
    int? MaxTotal);

/// <summary>
/// W6-A6 — the admin agent representation returned by GET /admin/agents/{id} and
/// /admin/agents (paged). Mirrors the fields the React admin UI consumed from the raw
/// <see cref="Agent"/> entity, ADDING the raw per-agent <see cref="CapacityOverride"/>
/// (null when fully inherited, so the UI can render "inherited" vs "overridden") plus the
/// resolved <see cref="EffectiveCapacity"/> (tenant default merged with the override,
/// MaxVoice pinned). The plaintext SIP password is deliberately NOT carried (admin
/// surfaces must never echo the secret — see AgentMeSipExposureTests).
/// The <see cref="Agent"/> entity's OfflineSince, CreatedBy, and UpdatedBy are intentionally
/// NOT projected (no admin-UI consumer today — add them here if a future supervisor surface needs them).
/// </summary>
internal sealed record AdminAgentResponseDto(
    string AgentId,
    string TenantId,
    string UserId,
    string DisplayName,
    AgentState State,
    AgentState? PendingState,
    string? PendingReason,
    DateTimeOffset? PendingSince,
    bool HasPendingPause,
    string? TeamId,
    IReadOnlyList<string> Skills,
    string? Extension,
    bool? AutoAnswer,
    bool CanAcceptWork,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    ChannelCapacityOverrideDto? CapacityOverride,
    ChannelCapacity EffectiveCapacity)
{
    public static AdminAgentResponseDto FromAgent(Agent agent, ChannelCapacity effective)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(effective);

        var o = agent.CapacityOverride;
        // Emit null when ALL five fields are null (fully inherited) so the UI can
        // distinguish "inherited" from "overridden"; otherwise project the sparse override.
        var overrideDto = (o.MaxVoice is null && o.MaxChat is null && o.MaxEmail is null
                && o.MaxSms is null && o.MaxTotal is null)
            ? null
            : new ChannelCapacityOverrideDto(o.MaxVoice, o.MaxChat, o.MaxEmail, o.MaxSms, o.MaxTotal);

        return new AdminAgentResponseDto(
            AgentId: agent.AgentId.Value,
            TenantId: agent.TenantId.Value,
            UserId: agent.UserId.Value,
            DisplayName: agent.DisplayName,
            State: agent.State,
            PendingState: agent.PendingState,
            PendingReason: agent.PendingReason,
            PendingSince: agent.PendingSince,
            HasPendingPause: agent.HasPendingPause,
            TeamId: agent.TeamId?.Value,
            Skills: agent.Skills,
            Extension: agent.Extension,
            AutoAnswer: agent.AutoAnswer,
            CanAcceptWork: agent.CanAcceptWork,
            CreatedAt: agent.CreatedAt,
            UpdatedAt: agent.UpdatedAt,
            CapacityOverride: overrideDto,
            EffectiveCapacity: effective);
    }
}

/// <summary>
/// ADR-0026: channel-aware queue membership specification at agent creation.
/// AllowedChannels=null means the agent is a member for all channels the
/// queue accepts (preserves implicit pre-v2.6.0 behavior). A populated list
/// restricts this membership to the listed channels only — both for routing
/// eligibility (Phase B) and Asterisk queue_members sync (Phase A: skipped
/// when voice not in AllowedChannels).
/// </summary>
internal sealed record QueueMembershipRequest(
    string QueueId,
    IReadOnlyList<string>? AllowedChannels = null,
    int? Penalty = null);

/// <summary>
/// ADR-0026 Phase A.6 — agent-centric membership projection used by the
/// <c>/admin/agents/{agentId}/queues</c> editor. Joins queue_memberships
/// with queues so the React UI renders queue names + channel multi-selects
/// without an N+1 fetch loop.
/// </summary>
internal sealed record AgentQueueMembershipDto(
    string QueueId,
    string QueueName,
    int Penalty,
    bool IsExcluded,
    IReadOnlyList<string>? AllowedChannels,
    string Source);
internal sealed record UpdateAgentRequest(
    string? DisplayName,
    string? TeamId,
    IReadOnlyList<string>? Skills,
    string? Extension = null,
    string? SipPassword = null,
    bool? AutoAnswer = null,
    // W6-A6 — optional per-agent capacity override. null = leave the existing override
    // untouched (consistent with the other optional fields); a populated DTO replaces it.
    ChannelCapacityOverrideDto? Capacity = null);

/// <summary>
/// W3 (A6) — request body for the admin force-offline lever. When
/// <paramref name="RevokeSessions"/> is true, the target agent's refresh-token
/// sessions are revoked (RevokeAllForUserAsync) so a wedged client cannot
/// silently re-establish a session after the supervisor kicks it Offline.
/// </summary>
internal sealed record ForceAgentOfflineRequest(bool RevokeSessions);

internal sealed record CreateTeamRequest(string Name);
internal sealed record UpdateTeamRequest(string? Name);

internal sealed record TeamDto(string Id, string Name, int MemberCount, DateTimeOffset CreatedAt);

internal sealed record UserDto(
    string Id,
    string Email,
    string DisplayName,
    string Role,
    string Status,
    DateTimeOffset CreatedAt);

internal sealed record QueueDto(
    string Id,
    string Name,
    bool IsActive,
    int? MaxWaiting,
    SlaPolicyTarget? SlaTargets,
    QueueOverflowRule? OverflowRule,
    WrapUpConfig WrapUp,
    IReadOnlyList<string> RequiredSkills,
    bool AutoAnswerDefault,
    DateTimeOffset CreatedAt);
