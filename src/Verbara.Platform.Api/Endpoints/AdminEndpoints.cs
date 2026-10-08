using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static partial class AdminEndpoints
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
        neutralGroup.MapPut("/users/{id}", UpdateUser)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
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
        if (user is null)
            return TypedResults.NotFound();

        // The tag an edit form sends back in If-Match (UpdateUser).
        UserAdminFieldsTag.SetOn(context.Response, user);
        return TypedResults.Ok(ToUserDto(user));
    }

    private static async Task<IResult> CreateUser(
        HttpContext context,
        [FromBody] CreateUserRequest body,
        [FromServices] IUserStore store,
        [FromServices] ITenantRoleStore tenantRoles,
        [FromServices] IRoleTemplateStore roleTemplates,
        [FromServices] IUserRoleStore userRoles,
        ILoggerFactory loggerFactory,
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
            await store.CreateAsync(user, ct);
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

        await GrantDefaultRoleAsync(user, tenantRoles, roleTemplates, userRoles, loggerFactory, ct);
        return Results.Created($"/admin/users/{user.UserId}", ToUserDto(user));
    }

    // Server-side permissions come from the user's RBAC roles, which the role migration would otherwise
    // attach only at the next start: grant the role the user's role maps to now (DefaultTenantRole).
    // Best-effort — the user exists either way, and that migration grants what this could not.
    private static async Task GrantDefaultRoleAsync(
        User user,
        ITenantRoleStore tenantRoles,
        IRoleTemplateStore roleTemplates,
        IUserRoleStore userRoles,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(AdminEndpoints).FullName!);
        try
        {
            var roleId = await DefaultTenantRole.GrantAsync(
                tenantRoles, roleTemplates, userRoles, user.TenantId, user.UserId, user.Role, ct);
            if (roleId is null)
                LogDefaultRoleUnavailable(logger, user.UserId.Value, user.TenantId.Value, user.Role);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDefaultRoleGrantFailed(logger, ex, user.UserId.Value, user.TenantId.Value, user.Role);
        }
    }

    [LoggerMessage(EventId = 7500, Level = LogLevel.Warning,
        Message = "User {UserId} in tenant {TenantId} holds no RBAC role yet: the tenant has no role for {Role} and its role template is unknown. The role migration at the next start grants it.")]
    private static partial void LogDefaultRoleUnavailable(ILogger logger, string userId, string tenantId, UserRole role);

    [LoggerMessage(EventId = 7501, Level = LogLevel.Warning,
        Message = "User {UserId} in tenant {TenantId} could not be granted the RBAC role for {Role}; the role migration at the next start grants it.")]
    private static partial void LogDefaultRoleGrantFailed(ILogger logger, Exception exception, string userId, string tenantId, UserRole role);

    // internal (not private) so Api.Tests can invoke it with a store that changes underneath it,
    // the way AuthEndpoints.Login is exercised directly.
    //
    // If-Match (optional): the tag GET returned for the form being saved. When it no longer names the
    // user's display name, role and status, the edit was made from a form someone else's change made
    // stale, and it is refused with 412 before anything is written; when it does, the write itself
    // only lands while the user still holds the values this request read (also 412 otherwise). Without
    // If-Match the edit is applied as before: only the fields that differ from what this request read.
    internal static async Task<Results<Ok<UserDto>, NotFound, ProblemHttpResult>> UpdateUser(
        string id,
        HttpContext context,
        [FromBody] UpdateUserRequest body,
        [FromServices] IUserStore store,
        [FromServices] SessionService sessions,
        [FromServices] IAuditService audit,
        [FromServices] ITenantRoleStore tenantRoles,
        [FromServices] IRoleTemplateStore roleTemplates,
        [FromServices] IUserRoleStore userRoles,
        [FromServices] PermissionResolver permissions,
        [FromServices] IAgentStore agents,
        [FromServices] AgentForceOfflineService forceOffline,
        [FromServices] ILicensedUserChangeWriter userWriter,
        PlatformEventBus eventBus,
        ILoggerFactory loggerFactory,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var user = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (user is null)
            return TypedResults.NotFound();

        var read = new AdminFields(user.DisplayName, user.Role, user.Status);
        var precondition = UserAdminFieldsTag.Evaluate(context.Request.Headers.IfMatch, read);
        if (precondition == IfMatchOutcome.NotMatched)
            return UserChangedSinceRead();

        // Only a value that differs from what this request read is written, and all of them in one
        // statement that changes an existing user only: a user deleted meanwhile is not recreated,
        // and no other column — password, MFA, lockout — is written from what this request read.
        var change = new AdminFieldsChange
        {
            DisplayName = body.DisplayName is { } displayName && displayName != user.DisplayName ? displayName : null,
            Role = body.Role is { } role && role != user.Role ? role : null,
            Status = body.Status is { } status && status != user.Status ? status : null,
            Expected = precondition == IfMatchOutcome.Matched ? read : null,
        };
        if (change is { DisplayName: null, Role: null, Status: null })
        {
            UserAdminFieldsTag.SetOn(context.Response, user);
            return TypedResults.Ok(ToUserDto(user));
        }

        // licensed-agent-metering (design D5) — a status change of a user who owns an agent commits with its
        // licensed-agent ledger row; every admin-field write goes through the same writer.
        var written = await LicensedUserWrites.UpdateAdminFieldsAsync(
            store, userWriter, tenantId, user.UserId, change, clock.UtcNow, CallerIdentity.ResolveUserId(context.User), ct);
        if (written.Outcome == AdminFieldsWriteOutcome.Stale)
            return UserChangedSinceRead();
        if (written is not { Outcome: AdminFieldsWriteOutcome.Written, Previous: { } previous, User: { } stored })
            return TypedResults.NotFound();

        // The store returns the values it replaced, so the RBAC move, the revocation and the audit
        // entries follow the transition that was actually written, not the one this request expected.
        await UserAdminChange.ApplyAsync(
            context,
            previous,
            stored,
            new UserAdminChangeServices(
                sessions, audit, eventBus, tenantRoles, roleTemplates, userRoles, permissions, agents, forceOffline,
                loggerFactory.CreateLogger(typeof(AdminEndpoints).FullName!)),
            ct);

        UserAdminFieldsTag.SetOn(context.Response, stored);
        return TypedResults.Ok(ToUserDto(stored));
    }

    private static ProblemHttpResult UserChangedSinceRead() =>
        TypedResults.Problem(
            title: "User changed",
            detail: "The user's display name, role or status changed after the version named in If-Match was read. Read the user again and reapply the change.",
            statusCode: StatusCodes.Status412PreconditionFailed,
            type: "https://verbara.platform/errors/precondition-failed");

    private static UserDto ToUserDto(User u) =>
        new(
            u.UserId.Value,
            u.Email,
            u.DisplayName,
            u.Role.ToString().ToLowerInvariant(),
            u.Status.ToString().ToLowerInvariant(),
            u.CreatedAt);

    // Deleting an account ends every access it still holds, and is audited.
    //  • The refresh-token lineage is revoked after the delete (so a token minted by a sign-in racing
    //    the delete is caught too). refresh_tokens has no foreign key to users: without this, the
    //    lineage would be refused only for as long as no row with this id exists.
    //  • Hub connections and SSE streams were authenticated once and would stay open: cut on every
    //    node. User-bound API keys stop on their own — they authenticate only while the owner exists.
    //  • Access tokens already issued stay valid until they expire (at most 15 minutes).
    //  • licensed-agent-metering (Q1) — a user who owns an agent is refused with 409 before anything is
    //    revoked or deleted: the admin deletes the agent first, so the agent's history stays attributable.
    private static async Task<IResult> DeleteUser(
        string id,
        HttpContext context,
        [FromServices] IUserStore store,
        [FromServices] IAgentStore agents,
        [FromServices] ILicensedUserChangeWriter userWriter,
        [FromServices] SessionService sessions,
        [FromServices] IAuditService audit,
        PlatformEventBus eventBus,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (await agents.GetByUserIdAsync(tenantId, EntityId.From(id), ct) is { } ownedAgent)
            return UserOwnsAgent(id, ownedAgent.AgentId.Value);

        // licensed-agent-metering (design D5) — the delete goes through the licensed-agent writer, which refuses
        // it under its lock when an agent was created for the user after the check above.
        var actorId = CallerIdentity.ResolveUserIdOrSystem(context.User);
        var deletion = await LicensedUserWrites.DeleteAsync(
            store, userWriter, tenantId, EntityId.From(id), actorId, deleteOwnedAgent: false, ct);
        if (deletion.OwnsAgent)
            return UserOwnsAgent(id, agentId: null);
        var deleted = deletion.UserDeleted;

        var ip = context.Connection.RemoteIpAddress?.ToString();
        var revokedSessions = await sessions.RevokeAllSessionsForUserAsync(
            tenantId.Value, actorId, id, ip, context.Request.Headers.UserAgent.FirstOrDefault(), ct);

        eventBus.Publish(new UserAccessRevokedEvent(tenantId.Value, id, UserAccessRevokedEvent.DeletedReason));

        if (deleted)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["revoked_sessions"] = revokedSessions.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ip"] = ip ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            };
            CallerIdentity.AddImpersonationContext(metadata, context.User);
            await audit.RecordAsync(
                tenantId,
                category: "auth",
                action: "user.deleted",
                severity: "warning",
                actorId: actorId,
                actorType: "user",
                targetId: id,
                targetType: "User",
                metadata: metadata,
                ct: ct);
        }

        return Results.NoContent();
    }

    private static IResult UserOwnsAgent(string userId, string? agentId) =>
        Results.Problem(
            title: "User owns an agent",
            detail: agentId is null
                ? $"User '{userId}' owns an agent. Delete the agent first, then the user."
                : $"User '{userId}' owns agent '{agentId}'. Delete the agent first (DELETE /api/v1/admin/agents/{agentId}), then the user.",
            statusCode: StatusCodes.Status409Conflict,
            type: "https://verbara.platform/errors/user-owns-agent");

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

        // ADR-0012 Ola-3 — the Asterisk realtime sync now rides IQueueStore.SaveAsync
        // (RealtimeSyncingQueueStore decorator); no endpoint-level Service-Locator resolve.
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

        // ADR-0012 Ola-3 — sync rides IQueueStore.SaveAsync (RealtimeSyncingQueueStore decorator).
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
        // ADR-0012 Ola-3 — RemoveQueueAsync now rides IQueueStore.DeleteAsync
        // (RealtimeSyncingQueueStore resolves the queue name before deleting).
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
        [FromServices] IUserStore users,
        [FromServices] IQueueStore queueStore,
        [FromServices] IQueueMembershipStore membershipStore,
        [FromServices] ICapacityDefaultsProvider defaultsProvider,
        [FromServices] IAuditService audit,
        [FromServices] ILicensedAgentChangeWriter writer,
        ILoggerFactory loggerFactory,
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

        // licensed-agent-metering (agent-identity-integrity) — an agent always names a user that exists
        // in the tenant: refused with 404 before anything is written.
        if (string.IsNullOrWhiteSpace(body.UserId)
            || await users.GetByIdAsync(tenantId, EntityId.From(body.UserId), ct) is null)
        {
            return Results.Problem(
                title: "User not found",
                detail: $"No user '{body.UserId}' exists in this tenant. Create the user first, then its agent.",
                statusCode: StatusCodes.Status404NotFound,
                type: "https://verbara.platform/errors/user-not-found");
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
        try
        {
            // licensed-agent-metering (design D5) — the agent row and its agent_created ledger row commit
            // together; the PJSIP upsert runs after the commit.
            await AgentLifecycle.CreateAsync(writer, store, agent, CallerIdentity.ResolveUserIdOrSystem(context.User), ct);
        }
        catch (EntityAlreadyExistsException)
        {
            // licensed-agent-metering (D3) — a user owns at most one agent per tenant. The unique
            // index on (tenant_id, user_id) refuses the second one, so two concurrent creations yield
            // one 201 and one 409, and no row is written for the refused one.
            return Results.Problem(
                title: "Agent already exists",
                detail: $"User '{body.UserId}' already owns an agent in this tenant; a user owns at most one agent.",
                statusCode: StatusCodes.Status409Conflict,
                type: "https://verbara.platform/errors/entity-already-exists");
        }

        await AgentLifecycle.TryAuditAsync(
            audit, loggerFactory.CreateLogger(typeof(AdminEndpoints).FullName!), AgentLifecycle.CreatedAction,
            agent, CallerIdentity.ResolveUserIdOrSystem(context.User), source: "admin", extraMetadata: null, ct);

        // W6-A6/M1 — best-effort audit when an override was supplied at creation (no old value).
        // Skip when the supplied override is itself all-null (== empty): that sets no real override,
        // so recording an old==new entry would be noise (consistent with the update-path check).
        var emptyOverride = new ChannelCapacityOverride();
        if (body.Capacity is not null && CapacityOverrideChanged(emptyOverride, agent.CapacityOverride))
            await RecordCapacityAuditAsync(audit, tenantId, GetCurrentUserId(context), agent.AgentId,
                oldOverride: emptyOverride, newOverride: agent.CapacityOverride, ct);

        // ADR-0012 Ola-3 — SyncAgentAsync (guarded on extension+password) rides the realtime-syncing
        // decorator's post-commit hook, run by AgentLifecycle.CreateAsync above.

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

                // ADR-0012 Ola-3 — AddQueueMemberAsync now rides
                // IQueueMembershipStore.SaveAsync (RealtimeSyncingQueueMembershipStore
                // decorator), which looks up queue.Name + agent.DisplayName from the
                // keyed inner stores and applies the SDK v2.6.0-pro voice-gate.
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

        // ADR-0012 Ola-3 — SyncAgentAsync (guarded on extension+password) now rides
        // IAgentStore.SaveAsync (RealtimeSyncingAgentStore decorator).
        var defaults = await defaultsProvider.GetDefaultsAsync(tenantId, ct);
        var effective = AgentCapacityResolver.ResolveEffective(agent.CapacityOverride, defaults);
        return TypedResults.Ok(AdminAgentResponseDto.FromAgent(agent, effective));
    }

    private static async Task<IResult> DeleteAgent(
        string id,
        HttpContext context,
        [FromServices] IAgentStore store,
        [FromServices] IQueueMembershipStore membershipStore,
        [FromServices] IAuditService audit,
        [FromServices] ILicensedAgentChangeWriter writer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agent = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (agent is null) return Results.NotFound();

        // licensed-agent-metering — the agent row and its agent_deleted ledger row commit together; the PJSIP
        // removal (ADR-0012 Ola-3) runs after the commit; audited as agent.deleted.
        var actorId = CallerIdentity.ResolveUserIdOrSystem(context.User);
        if (!await AgentLifecycle.DeleteAsync(writer, store, membershipStore, tenantId, agent.AgentId, actorId, ct))
            return Results.NotFound();
        await AgentLifecycle.TryAuditAsync(
            audit, loggerFactory.CreateLogger(typeof(AdminEndpoints).FullName!), AgentLifecycle.DeletedAction,
            agent, actorId, source: "admin", extraMetadata: null, ct);
        return Results.NoContent();
    }

    // ADR-0012 Ola-3 — the best-effort realtime-sync deferral logging (EventId 4130) that
    // used to live here (LogRealtimeSyncDeferred / RealtimeSyncDeferred) moved with the sync
    // itself into the store decorators (RealtimeSyncDeferralLog + RealtimeSyncing*Store). The
    // admin write-paths no longer resolve IRealtimeSyncService at all — the storage seam does.

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
        [FromServices] AgentForceOfflineService forceOffline,
        [FromServices] IRefreshTokenStore refreshTokenStore,
        [FromServices] IAuditService audit,
        [FromServices] TimeProvider clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var callerUserId = GetCurrentUserId(context);

        // licensed-agent-metering (D2) — the teardown (Offline + liveness removal + paused, under the
        // per-agent lock, and the state event on a real transition) is shared with a user status change
        // that leaves access.
        if (await forceOffline.ApplyAsync(tenantId, EntityId.From(id), ct) is not { } forced)
            return Results.NotFound();
        var agent = forced.Agent;
        var oldState = forced.OldState;

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
