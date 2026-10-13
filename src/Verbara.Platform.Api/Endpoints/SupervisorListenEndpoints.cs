using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// supervisor-silent-listen (design D9, D12, D13): the silent AUDIO listen routes, keyed by conversation id, and the
/// administrator's listen phone. They are distinct by path and by id kind from the Agent Assist transcript routes
/// (<c>/supervisor/sessions/{sessionId}/…</c>, which move no audio), and none of them uses the word "whisper".
/// Mapped from <c>MapSupervisorEndpoints</c> and from the admin neutral group, so <c>Program.cs</c> gains no route line;
/// always mapped, so the OpenAPI capture sees them. Without AMI the listen service is absent, and start, stop and
/// current answer <c>voice-unavailable</c>.
/// </summary>
internal static partial class SupervisorListenEndpoints
{
    /// <summary>The permission every audio-listen route requires, in addition to the group's SupervisorPlus role.</summary>
    public const string MonitorPermission = "contacts:conversation:monitor";

    private const string MonitorPolicy = "Permission:" + MonitorPermission;

    /// <summary>The audit action of a listen-phone change.</summary>
    public const string ListenPhoneSetAction = "user.listen_phone_set";

    /// <summary>The supervisor routes, on the <c>/supervisor</c> group (SupervisorPlus, operational tenant).</summary>
    public static void MapSupervisorListenRoutes(this RouteGroupBuilder group)
    {
        group.MapPost("/listen-endpoint", ProvisionListenEndpoint)
            .WithSummary("Provision the caller's receive-only listen endpoint (silent audio listen)")
            .Produces<SupervisorListenEndpointDto>(StatusCodes.Status200OK)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status403Forbidden)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status409Conflict);

        group.MapPost("/conversations/{id}/listen", StartListen)
            .WithSummary("Start a silent audio listen on a live voice conversation")
            .Produces<SupervisorListenDto>(StatusCodes.Status200OK)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status400BadRequest)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status403Forbidden)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status404NotFound)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status409Conflict)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status502BadGateway)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/listen", (Delegate)GetCurrentListen)
            .WithSummary("The caller's active silent audio listen")
            .Produces<SupervisorListenDto>(StatusCodes.Status200OK)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status403Forbidden)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status404NotFound)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/conversations/{id}/listen/stop", StopListen)
            .WithSummary("Stop the caller's silent audio listen on a conversation")
            .Produces<SupervisorListenStoppedDto>(StatusCodes.Status200OK)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status403Forbidden)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status404NotFound)
            .Produces<SupervisorListenRefusal>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>The administrator's listen-phone routes, on the admin neutral group (AdminOnly).</summary>
    public static void MapListenPhoneRoutes(this RouteGroupBuilder neutralGroup)
    {
        neutralGroup.MapGet("/users/{id}/listen-phone", GetListenPhone)
            .WithSummary("A user's listen phone (silent audio listen, phone destination)")
            .Produces<ListenPhoneDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        neutralGroup.MapPut("/users/{id}/listen-phone", SetListenPhone)
            .WithSummary("Set or clear a user's listen phone (strict E.164)")
            .Produces<ListenPhoneDto>(StatusCodes.Status200OK)
            .Produces<ListenPhoneRefusal>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    // ─── Supervisor routes ────────────────────────────────────────────────────

    private static async Task<Results<Ok<SupervisorListenEndpointDto>, JsonHttpResult<SupervisorListenRefusal>>> ProvisionListenEndpoint(
        HttpContext context,
        [FromServices] ISupervisorListenProfileStore profiles,
        [FromServices] IUserStore users,
        [FromServices] IRealtimeSyncService sync,
        [FromServices] IClock clock,
        [FromServices] ILogger<SupervisorListenEndpointProvisioner> logger,
        CancellationToken ct)
    {
        if (await CallerAsync(context) is not { } supervisor)
            return Refuse(SupervisorListenRefusal.NotPermitted, null);

        var provisioner = new SupervisorListenEndpointProvisioner(
            profiles, users, sync, context.RequestServices.GetService<EndpointProfileStoreBase>(), clock, logger);
        var credentials = await provisioner.ProvisionAsync(TenantOf(context), supervisor, ct);
        return credentials is null
            ? Refuse(SupervisorListenRefusal.NoSupervisorEndpoint, null)
            : TypedResults.Ok(credentials);
    }

    private static async Task<Results<Ok<SupervisorListenDto>, JsonHttpResult<SupervisorListenRefusal>>> StartListen(
        string id,
        [FromBody] SupervisorListenStartRequest? body,
        HttpContext context,
        CancellationToken ct)
    {
        if (await CallerAsync(context) is not { } supervisor)
            return Refuse(SupervisorListenRefusal.NotPermitted, id);
        if (!ListenVocabulary.IsDestination(body?.Destination))
            return Refuse(SupervisorListenRefusal.InvalidDestination, id);
        if (context.RequestServices.GetService<SupervisorListenService>() is not { } listen)
            return Refuse(SupervisorListenRefusal.VoiceUnavailable, id);

        var result = await listen.StartAsync(context.User, TenantOf(context), supervisor, id, body!.Destination!, ct);
        return result.Refusal is { } refusal ? Refuse(refusal) : TypedResults.Ok(result.Listen!);
    }

    private static async Task<Results<Ok<SupervisorListenDto>, JsonHttpResult<SupervisorListenRefusal>>> GetCurrentListen(
        HttpContext context)
    {
        if (await CallerAsync(context) is not { } supervisor)
            return Refuse(SupervisorListenRefusal.NotPermitted, null);
        if (context.RequestServices.GetService<SupervisorListenService>() is not { } listen)
            return Refuse(SupervisorListenRefusal.VoiceUnavailable, null);

        var result = listen.GetCurrent(TenantOf(context), supervisor);
        return result.Refusal is { } refusal ? Refuse(refusal) : TypedResults.Ok(result.Listen!);
    }

    private static async Task<Results<Ok<SupervisorListenStoppedDto>, JsonHttpResult<SupervisorListenRefusal>>> StopListen(
        string id,
        HttpContext context,
        CancellationToken ct)
    {
        if (await CallerAsync(context) is not { } supervisor)
            return Refuse(SupervisorListenRefusal.NotPermitted, id);
        if (context.RequestServices.GetService<SupervisorListenService>() is not { } listen)
            return Refuse(SupervisorListenRefusal.VoiceUnavailable, id);

        var result = await listen.StopAsync(context.User, TenantOf(context), supervisor, id, ct);
        return result.Refusal is { } refusal ? Refuse(refusal) : TypedResults.Ok(result.Stopped!);
    }

    /// <summary>
    /// The caller's user id when it holds <see cref="MonitorPermission"/> (design D9: evaluated in the handler, so a
    /// denial answers a code, not the bare 403 of a route-level policy); otherwise null.
    /// </summary>
    private static async Task<EntityId?> CallerAsync(HttpContext context)
    {
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        if (!(await authorization.AuthorizeAsync(context.User, MonitorPolicy)).Succeeded)
            return null;
        return CallerIdentity.ResolveUserId(context.User) is { } userId && EntityId.IsValid(userId) ? EntityId.From(userId) : null;
    }

    private static JsonHttpResult<SupervisorListenRefusal> Refuse(string code, string? conversationId) =>
        Refuse(SupervisorListenRefusal.For(code, conversationId));

    private static JsonHttpResult<SupervisorListenRefusal> Refuse(SupervisorListenRefusal refusal) =>
        TypedResults.Json(refusal, ApiJsonContext.Default.SupervisorListenRefusal, statusCode: SupervisorListenRefusal.StatusFor(refusal.Error));

    // ─── Admin listen phone ───────────────────────────────────────────────────

    // ASCII digits only: \d also matches every Unicode decimal digit (Arabic-Indic, fullwidth, Devanagari…), which no
    // trunk dials. Strict E.164: a leading '+', no leading zero, 7 to 15 digits in all.
    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164();

    private static async Task<Results<Ok<ListenPhoneDto>, NotFound>> GetListenPhone(
        string id,
        HttpContext context,
        [FromServices] IUserStore users,
        [FromServices] ISupervisorListenProfileStore profiles,
        CancellationToken ct)
    {
        var tenantId = TenantOf(context);
        if (!EntityId.IsValid(id) || await users.GetByIdAsync(tenantId, EntityId.From(id), ct) is null)
            return TypedResults.NotFound();

        var profile = await profiles.GetAsync(tenantId, EntityId.From(id), ct);
        return TypedResults.Ok(new ListenPhoneDto(id, profile?.PhoneNumber));
    }

    private static async Task<Results<Ok<ListenPhoneDto>, JsonHttpResult<ListenPhoneRefusal>, NotFound>> SetListenPhone(
        string id,
        [FromBody] ListenPhoneRequest? body,
        HttpContext context,
        [FromServices] IUserStore users,
        [FromServices] ISupervisorListenProfileStore profiles,
        [FromServices] IAuditService audit,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        var tenantId = TenantOf(context);
        if (!EntityId.IsValid(id) || await users.GetByIdAsync(tenantId, EntityId.From(id), ct) is null)
            return TypedResults.NotFound();

        var raw = body?.PhoneNumber;
        var number = raw?.Trim();
        if (number is not null && !E164().IsMatch(number))
            return TypedResults.Json(ListenPhoneRefusal.For(raw), ApiJsonContext.Default.ListenPhoneRefusal,
                statusCode: StatusCodes.Status400BadRequest);

        var userId = EntityId.From(id);
        var now = clock.UtcNow;
        var profile = await profiles.GetAsync(tenantId, userId, ct)
            ?? new SupervisorListenProfile { TenantId = tenantId, UserId = userId, CreatedAt = now };
        profile.PhoneNumber = number;
        profile.UpdatedAt = now;
        await profiles.SaveAsync(profile, ct);

        await AuditPhoneSetAsync(context, audit, tenantId, id, number, ct);
        return TypedResults.Ok(new ListenPhoneDto(id, number));
    }

    /// <summary>Audits a listen-phone change with only the number's last four digits (design D8); best-effort.</summary>
    private static async Task AuditPhoneSetAsync(
        HttpContext context, IAuditService audit, TenantId tenantId, string userId, string? number, CancellationToken ct)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["phone_last4"] = number is null ? string.Empty : number[^4..],
            ["cleared"] = number is null ? "true" : "false",
        };
        CallerIdentity.AddImpersonationContext(metadata, context.User);
        try
        {
            await audit.RecordAsync(
                tenantId,
                category: "users",
                action: ListenPhoneSetAction,
                severity: "info",
                actorId: CallerIdentity.ResolveUserIdOrSystem(context.User),
                actorType: "user",
                targetId: userId,
                targetType: "User",
                metadata: metadata,
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SupervisorListenEndpoints).FullName!);
            LogPhoneAuditFailed(logger, ex, tenantId.Value);
        }
    }

    private static TenantId TenantOf(HttpContext context) =>
        context.Items.TryGetValue("TenantId", out var value) && value is TenantId tenantId
            ? tenantId
            : throw new InvalidOperationException("Tenant ID not resolved");

    [LoggerMessage(EventId = 7531, Level = LogLevel.Warning,
        Message = "The user.listen_phone_set audit entry in tenant {TenantId} could not be written; the change itself was made.")]
    private static partial void LogPhoneAuditFailed(ILogger logger, Exception exception, string tenantId);
}
