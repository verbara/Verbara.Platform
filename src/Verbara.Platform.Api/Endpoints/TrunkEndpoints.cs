using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Voice;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Dialer.Routing;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

internal static class TrunkEndpoints
{
    public static void MapTrunkEndpoints(this IEndpointRouteBuilder app)
    {
        var trunks = app.MapGroup("/admin/trunks").RequireAuthorization("AdminOnly").RequireOperationalTenant();

        trunks.MapGet("/", ListTrunks);
        trunks.MapGet("/active", ListActiveTrunks);
        trunks.MapGet("/by-name/{name}", GetTrunkByName);
        trunks.MapGet("/{id:long}", GetTrunk);
        trunks.MapPost("/", CreateTrunk);
        trunks.MapPut("/{id:long}", UpdateTrunk);
        trunks.MapDelete("/{id:long}", DeleteTrunk);
        trunks.MapPost("/{id:long}/test-connectivity", TestTrunkConnectivity);
    }

    // ─── Handlers ────────────────────────────────────────────────────────────

    private static async Task<IResult> ListTrunks(
        HttpContext context,
        [FromServices] TrunkStoreBase trunkStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var items = await trunkStore.ListAsync(tenantId, ct);
        return Results.Ok(items.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetTrunk(
        long id,
        HttpContext context,
        [FromServices] TrunkStoreBase trunkStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var trunk = await trunkStore.GetAsync(id, tenantId, ct);
        return trunk is null ? Results.NotFound() : Results.Ok(MapToDto(trunk));
    }

    private static async Task<IResult> CreateTrunk(
        HttpContext context,
        [FromBody] CreateTrunkRequest body,
        TrunkStoreBase trunkStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        if (!string.IsNullOrEmpty(body.MatchHost) && !IsValidMatchHost(body.MatchHost))
            return Results.BadRequest();

        var invalidCodecs = KnownCodecs.InvalidTokens(body.Codecs);
        if (invalidCodecs.Count > 0)
            return Results.BadRequest(new CodecValidationError([.. invalidCodecs]));

        var trunk = new Trunk
        {
            Name = body.Name,
            DisplayName = body.DisplayName,
            Type = ParseTrunkType(body.Type),
            IsActive = body.IsActive,
            MaxChannels = body.MaxChannels,
            Transport = body.Transport,
            Codecs = body.Codecs,
            AuthUsername = body.AuthUsername,
            AuthPassword = body.AuthPassword,
            RegistrationUri = body.RegistrationUri,
            ClientUri = body.ClientUri,
            Context = body.Context,
            MatchHost = body.MatchHost,
        };
        var id = await trunkStore.CreateAsync(trunk, tenantId, ct);
        trunk.Id = id;
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "trunk.created", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(System.Globalization.CultureInfo.InvariantCulture), targetType: "trunk",
            changes: new AuditChanges(Before: null, After: MapToDto(trunk)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Created($"/admin/trunks/{id}", MapToDto(trunk));
    }

    private static async Task<IResult> UpdateTrunk(
        long id,
        HttpContext context,
        [FromBody] UpdateTrunkRequest body,
        TrunkStoreBase trunkStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var trunk = await trunkStore.GetAsync(id, tenantId, ct);
        if (trunk is null)
            return Results.NotFound();

        var before = MapToDto(trunk);

        if (body.Name is not null) trunk.Name = body.Name;
        if (body.DisplayName is not null) trunk.DisplayName = body.DisplayName;
        if (body.Type is not null) trunk.Type = ParseTrunkType(body.Type);
        if (body.IsActive.HasValue) trunk.IsActive = body.IsActive.Value;
        if (body.MaxChannels.HasValue) trunk.MaxChannels = body.MaxChannels.Value;
        if (body.Transport is not null) trunk.Transport = body.Transport;
        if (body.Codecs is not null)
        {
            var invalidCodecs = KnownCodecs.InvalidTokens(body.Codecs);
            if (invalidCodecs.Count > 0)
                return Results.BadRequest(new CodecValidationError([.. invalidCodecs]));
            trunk.Codecs = body.Codecs;
        }
        if (body.AuthUsername is not null) trunk.AuthUsername = body.AuthUsername;
        if (body.AuthPassword is not null) trunk.AuthPassword = body.AuthPassword;
        if (body.RegistrationUri is not null) trunk.RegistrationUri = body.RegistrationUri;
        if (body.ClientUri is not null) trunk.ClientUri = body.ClientUri;
        if (body.Context is not null) trunk.Context = body.Context;
        if (body.MatchHost is not null)
        {
            if (!string.IsNullOrWhiteSpace(body.MatchHost) && !IsValidMatchHost(body.MatchHost))
                return Results.BadRequest();
            // Empty/whitespace is the explicit clear sentinel — stored as null so the engine
            // deletes the ps_endpoint_id_ips identify row (IP-ACL removal).
            trunk.MatchHost = string.IsNullOrWhiteSpace(body.MatchHost) ? null : body.MatchHost;
        }

        await trunkStore.UpdateAsync(trunk, tenantId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "trunk.updated", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(System.Globalization.CultureInfo.InvariantCulture), targetType: "trunk",
            changes: new AuditChanges(Before: before, After: MapToDto(trunk)),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.Ok(MapToDto(trunk));
    }

    private static async Task<IResult> DeleteTrunk(
        long id,
        HttpContext context,
        [FromServices] TrunkStoreBase trunkStore,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var before = await trunkStore.GetAsync(id, tenantId, ct);
        await trunkStore.DeleteAsync(id, tenantId, ct);
        await audit.RecordAsync(
            new TenantId(tenantId), category: "config", action: "trunk.deleted", severity: "info",
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
            targetId: id.ToString(System.Globalization.CultureInfo.InvariantCulture), targetType: "trunk",
            changes: new AuditChanges(Before: before is null ? null : MapToDto(before), After: null),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListActiveTrunks(
        HttpContext context,
        [FromServices] TrunkStoreBase trunkStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var trunks = await trunkStore.ListActiveAsync(tenantId, ct);
        return Results.Ok(trunks.Select(MapToDto).ToList());
    }

    private static async Task<IResult> GetTrunkByName(
        string name,
        HttpContext context,
        [FromServices] TrunkStoreBase trunkStore,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var trunk = await trunkStore.GetByNameAsync(name, tenantId, ct);
        return trunk is null ? Results.NotFound() : Results.Ok(MapToDto(trunk));
    }

    /// <summary>
    /// Runs the <c>pjsip show ...</c> diagnostic queries server-side (over AMI) and returns a structured
    /// report — so the operator no longer has to SSH and run them by hand (manual §3.3). Always 200 with
    /// the diagnostic, even when <c>ok=false</c> (it is a report, not a command). 404 only when the trunk
    /// does not exist for the caller tenant. When voice-AMI is disabled, <see cref="ITrunkConnectivityTester"/>
    /// is not registered, so a clean "AMI no disponible" result is returned.
    /// </summary>
    private static async Task<IResult> TestTrunkConnectivity(
        long id,
        HttpContext context,
        [FromServices] TrunkStoreBase trunkStore,
        [FromServices] ITrunkConnectivityTester? tester,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        if (tester is null)
        {
            // Voice-AMI not configured: the trunk must still exist for the tenant (so a bad id 404s),
            // but there is no live AMI connection to query.
            var trunk = await trunkStore.GetAsync(id, tenantId, ct);
            if (trunk is null)
                return Results.NotFound();
            return Results.Ok(new TrunkConnectivityResult(
                id, $"t-{id}", EndpointFound: false, AuthMode: "none",
                Registered: null, IdentifyPresent: null, Reachable: null, Ok: false,
                Messages: ["AMI no disponible — la verificación de voz no está configurada en este servidor"]));
        }

        var result = await tester.TestAsync(new TenantId(tenantId), id, ct);
        if (!result.EndpointFound && result.Messages.Contains(ITrunkConnectivityTester.TrunkNotFoundMessage))
            return Results.NotFound();
        return Results.Ok(result);
    }

    // ─── Mapping Helpers ──────────────────────────────────────────────────────

    private static TrunkDto MapToDto(Trunk t) =>
        new(t.Id, t.Name, t.DisplayName, t.Type.ToString(), t.IsActive, t.MaxChannels,
            t.Transport, t.Codecs, t.AuthUsername, t.RegistrationUri, t.ClientUri, t.Context, t.MatchHost);

    /// <summary>
    /// Validates a trunk <c>MatchHost</c> as a well-formed IP address or CIDR (host[/prefix]).
    /// Rejects garbage/typos so a malformed value can't silently disable Asterisk IP
    /// identification (Asterisk drops a malformed identify object at load with no operator signal).
    /// </summary>
    private static bool IsValidMatchHost(string value)
    {
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
            return System.Net.IPAddress.TryParse(value, out _);

        var host = value[..slash];
        if (!System.Net.IPAddress.TryParse(host, out var ip))
            return false;
        if (!int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var prefix))
            return false;
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return prefix >= 0 && prefix <= max;
    }

    private static TrunkType ParseTrunkType(string type) =>
        type.ToLowerInvariant() switch
        {
            "sip" => TrunkType.Sip,
            "iax2" => TrunkType.Iax2,
            "dahdi" => TrunkType.Dahdi,
            "pjsip" => TrunkType.Pjsip,
            _ => TrunkType.Pjsip,
        };

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid.Value;

        throw new InvalidOperationException("Tenant ID not resolved");
    }
}

// ─── Request/Response DTOs ─────────────────────────────────────────────────────

internal sealed record CreateTrunkRequest(
    string Name, string? DisplayName, string Type, bool IsActive, int MaxChannels,
    string? Transport, string? Codecs, string? AuthUsername, string? AuthPassword,
    string? RegistrationUri, string? ClientUri, string? Context, string? MatchHost);

internal sealed record UpdateTrunkRequest(
    string? Name, string? DisplayName, string? Type, bool? IsActive, int? MaxChannels,
    string? Transport, string? Codecs, string? AuthUsername, string? AuthPassword,
    string? RegistrationUri, string? ClientUri, string? Context, string? MatchHost);

internal sealed record TrunkDto(
    long Id, string Name, string? DisplayName, string Type, bool IsActive, int MaxChannels,
    string? Transport, string? Codecs, string? AuthUsername,
    string? RegistrationUri, string? ClientUri, string? Context, string? MatchHost);
