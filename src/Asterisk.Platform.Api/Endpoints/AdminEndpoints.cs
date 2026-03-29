using Asterisk.Platform.Core;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Sdk.Pro.Realtime;
using Asterisk.Sdk.Pro.Realtime.Models;
using Microsoft.AspNetCore.Mvc;

namespace Asterisk.Platform.Api.Endpoints;

internal static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").RequireAuthorization("AdminOnly");

        // Users
        group.MapGet("/users", ListUsers);
        group.MapGet("/users/{id}", GetUser);
        group.MapPost("/users", CreateUser);
        group.MapPut("/users/{id}", UpdateUser);
        group.MapDelete("/users/{id}", DeleteUser);

        // Queues
        group.MapGet("/queues", ListQueues);
        group.MapGet("/queues/{id}", GetQueue);
        group.MapPost("/queues", CreateQueue);
        group.MapPut("/queues/{id}", UpdateQueue);
        group.MapDelete("/queues/{id}", DeleteQueue);

        // Queue Members
        group.MapPost("/queue-members", AddQueueMember);
        group.MapDelete("/queue-members/{queueId}/{agentId}", RemoveQueueMember);

        // Agents
        group.MapGet("/agents", ListAgents);
        group.MapGet("/agents/{id}", GetAgent);
        group.MapPost("/agents", CreateAgent);
        group.MapPut("/agents/{id}", UpdateAgent);
        group.MapDelete("/agents/{id}", DeleteAgent);

        // Teams
        group.MapGet("/teams", ListTeams);
        group.MapGet("/teams/{id}", GetTeam);
        group.MapPost("/teams", CreateTeam);
        group.MapPut("/teams/{id}", UpdateTeam);
        group.MapDelete("/teams/{id}", DeleteTeam);
    }

    // ─── Users ────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListUsers(
        HttpContext context,
        [FromServices] IUserStore store,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, new PagedQuery { Page = page, PageSize = pageSize }, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetUser(
        string id,
        HttpContext context,
        [FromServices] IUserStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var user = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return user is null ? Results.NotFound() : Results.Ok(user);
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
            CreatedAt = clock.UtcNow,
        };
        await store.SaveAsync(user, ct);
        return Results.Created($"/api/admin/users/{user.UserId}", user);
    }

    private static async Task<IResult> UpdateUser(
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
            return Results.NotFound();

        user.DisplayName = body.DisplayName ?? user.DisplayName;
        if (body.Role.HasValue) user.Role = body.Role.Value;
        if (body.Status.HasValue) user.Status = body.Status.Value;
        user.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(user, ct);
        return Results.Ok(user);
    }

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

    private static async Task<IResult> ListQueues(
        HttpContext context,
        [FromServices] IQueueStore store,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, new PagedQuery { Page = page, PageSize = pageSize }, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetQueue(
        string id,
        HttpContext context,
        [FromServices] IQueueStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return queue is null ? Results.NotFound() : Results.Ok(queue);
    }

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
            CreatedAt = clock.UtcNow,
        };
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

        return Results.Created($"/api/admin/queues/{queue.QueueId}", queue);
    }

    private static async Task<IResult> UpdateQueue(
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
            return Results.NotFound();

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

        return Results.Ok(queue);
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

    private static async Task<IResult> AddQueueMember(
        HttpContext context, [FromBody] AddQueueMemberRequest body,
        [FromServices] IQueueStore queueStore, [FromServices] IAgentStore agentStore,
        [FromServices] IQueueMembershipStore membershipStore, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await queueStore.GetByIdAsync(tenantId, EntityId.From(body.QueueId), ct);
        var agent = await agentStore.GetByIdAsync(tenantId, EntityId.From(body.AgentId), ct);
        if (queue is null || agent is null) return Results.NotFound();

        await membershipStore.SaveAsync(new QueueMembership
        {
            TenantId = tenantId,
            QueueId = EntityId.From(body.QueueId),
            AgentId = EntityId.From(body.AgentId),
            Penalty = body.Penalty ?? 0,
            Source = MembershipSource.Manual,
            IsExcluded = false,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null)
        {
            await syncService.AddQueueMemberAsync(tenantId, queue.Name, agent.AgentId.Value, agent.DisplayName, body.Penalty ?? 0, ct);
        }
        return Results.Created($"/api/admin/queue-members/{body.QueueId}/{body.AgentId}", null);
    }

    private static async Task<IResult> RemoveQueueMember(
        string queueId, string agentId, HttpContext context,
        [FromServices] IQueueStore queueStore, [FromServices] IAgentStore agentStore,
        [FromServices] IQueueMembershipStore membershipStore, CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var queue = await queueStore.GetByIdAsync(tenantId, EntityId.From(queueId), ct);
        var agent = await agentStore.GetByIdAsync(tenantId, EntityId.From(agentId), ct);
        if (queue is null || agent is null) return Results.NotFound();

        await membershipStore.DeleteAsync(tenantId, EntityId.From(queueId), EntityId.From(agentId), ct);

        var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
        if (syncService is not null)
        {
            await syncService.RemoveQueueMemberAsync(tenantId, queue.Name, agent.AgentId.Value, ct);
        }
        return Results.NoContent();
    }

    // ─── Agents ───────────────────────────────────────────────────────────────

    private static async Task<IResult> ListAgents(
        HttpContext context,
        [FromServices] IAgentStore store,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, new AgentQuery { Page = page, PageSize = pageSize }, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetAgent(
        string id,
        HttpContext context,
        [FromServices] IAgentStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agent = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return agent is null ? Results.NotFound() : Results.Ok(agent);
    }

    private static async Task<IResult> CreateAgent(
        HttpContext context,
        [FromBody] CreateAgentRequest body,
        [FromServices] IAgentStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
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
        await store.SaveAsync(agent, ct);

        if (!string.IsNullOrEmpty(agent.Extension) && !string.IsNullOrEmpty(agent.SipPassword))
        {
            var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
            if (syncService is not null)
            {
                try { await syncService.SyncAgentAsync(tenantId, agent.AgentId.Value, agent.DisplayName, agent.Extension, agent.SipPassword, ct: ct); }
                catch { }
            }
        }

        return Results.Created($"/api/admin/agents/{agent.AgentId}", agent);
    }

    private static async Task<IResult> UpdateAgent(
        string id,
        HttpContext context,
        [FromBody] UpdateAgentRequest body,
        [FromServices] IAgentStore store,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var agent = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        if (agent is null)
            return Results.NotFound();

        if (body.DisplayName is not null) agent.DisplayName = body.DisplayName;
        if (body.TeamId is not null) agent.TeamId = EntityId.From(body.TeamId);
        if (body.Skills is not null) agent.Skills = body.Skills;
        if (body.Extension is not null) agent.Extension = body.Extension;
        if (body.SipPassword is not null) agent.SipPassword = body.SipPassword;
        agent.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(agent, ct);

        if (!string.IsNullOrEmpty(agent.Extension) && !string.IsNullOrEmpty(agent.SipPassword))
        {
            var syncService = context.RequestServices.GetService<IRealtimeSyncService>();
            if (syncService is not null)
            {
                try { await syncService.SyncAgentAsync(tenantId, agent.AgentId.Value, agent.DisplayName, agent.Extension, agent.SipPassword, ct: ct); }
                catch { }
            }
        }

        return Results.Ok(agent);
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

    // ─── Teams ────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListTeams(
        HttpContext context,
        [FromServices] ITeamStore store,
        int page = 1,
        int pageSize = 25,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId(context);
        var result = await store.ListAsync(tenantId, new PagedQuery { Page = page, PageSize = pageSize }, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetTeam(
        string id,
        HttpContext context,
        [FromServices] ITeamStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var team = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return team is null ? Results.NotFound() : Results.Ok(team);
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
        return Results.Created($"/api/admin/teams/{team.TeamId}", team);
    }

    private static async Task<IResult> UpdateTeam(
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
            return Results.NotFound();

        if (body.Name is not null) team.Name = body.Name;
        team.UpdatedAt = clock.UtcNow;
        await store.SaveAsync(team, ct);
        return Results.Ok(team);
    }

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

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request DTOs ─────────────────────────────────────────────────────────────

internal sealed record CreateUserRequest(string Email, string DisplayName, UserRole Role);
internal sealed record UpdateUserRequest(string? DisplayName, UserRole? Role, UserStatus? Status);

internal sealed record CreateQueueRequest(
    string Name,
    SlaPolicyTargetDto? SlaTargets = null,
    QueueOverflowRuleDto? OverflowRule = null,
    WrapUpConfigDto? WrapUp = null,
    int? MaxWaiting = null,
    IReadOnlyList<string>? RequiredSkills = null,
    string? Timezone = null);

internal sealed record UpdateQueueRequest(
    string? Name = null,
    bool? IsActive = null,
    SlaPolicyTargetDto? SlaTargets = null,
    QueueOverflowRuleDto? OverflowRule = null,
    WrapUpConfigDto? WrapUp = null,
    int? MaxWaiting = null,
    IReadOnlyList<string>? RequiredSkills = null,
    string? Timezone = null);

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

internal sealed record CreateAgentRequest(string UserId, string DisplayName, string? Extension = null, string? SipPassword = null);
internal sealed record UpdateAgentRequest(string? DisplayName, string? TeamId, IReadOnlyList<string>? Skills, string? Extension = null, string? SipPassword = null);

internal sealed record CreateTeamRequest(string Name);
internal sealed record UpdateTeamRequest(string? Name);
