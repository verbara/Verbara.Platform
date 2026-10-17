using Verbara.Platform.Api.Endpoints.Shared;
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
    /// <summary>400: a request set <c>registrationUri</c> to an empty or blank value.</summary>
    internal const string RegistrationUriEmptyCode = "registration-uri-empty";

    /// <summary>400: a trunk with a registration URI would have no client URI (Asterisk rejects the registration).</summary>
    internal const string ClientUriRequiredCode = "client-uri-required";

    /// <summary>400: <c>registrationUri</c> or <c>clientUri</c> is not a <c>sip:</c> / <c>sips:</c> URI (field in details).</summary>
    internal const string InvalidSipUriCode = "invalid-sip-uri";

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

        var registrationError = ValidateRegistration(
            body.RegistrationUri, body.ClientUri, body.RegistrationUri, body.ClientUri);
        if (registrationError is not null)
            return Results.BadRequest(registrationError);

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

        // Validated against the stored values before anything is applied: a request that sets only one
        // of the two URIs is checked together with the other one as stored. A URI the request sends back
        // unchanged counts as not requested: the console's edit form echoes both stored URIs on every save,
        // so a trunk stored before this rule must stay editable for its other fields.
        var requestedRegistrationUri = string.Equals(body.RegistrationUri, trunk.RegistrationUri, StringComparison.Ordinal)
            ? null : body.RegistrationUri;
        var requestedClientUri = string.Equals(body.ClientUri, trunk.ClientUri, StringComparison.Ordinal)
            ? null : body.ClientUri;
        var registrationError = ValidateRegistration(
            requestedRegistrationUri, requestedClientUri,
            body.RegistrationUri ?? trunk.RegistrationUri, body.ClientUri ?? trunk.ClientUri);
        if (registrationError is not null)
            return Results.BadRequest(registrationError);

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

    /// <summary>
    /// #385 — a registration trunk must carry a registration Asterisk can send: a non-blank <c>sip:</c> /
    /// <c>sips:</c> registration URI and a <c>sip:</c> / <c>sips:</c> client URI (the Realtime sync writes a
    /// missing client URI as an empty <c>client_uri</c>, which Asterisk refuses). Only the fields the request
    /// carries (<paramref name="requestedRegistrationUri"/>, <paramref name="requestedClientUri"/>) are checked,
    /// so a trunk stored before this rule stays editable for its other fields; the client-URI requirement is
    /// checked on the resulting values (<paramref name="registrationUri"/>, <paramref name="clientUri"/>).
    /// </summary>
    private static ErrorDetailResponse? ValidateRegistration(
        string? requestedRegistrationUri, string? requestedClientUri, string? registrationUri, string? clientUri)
    {
        if (requestedRegistrationUri is null && requestedClientUri is null)
            return null;

        if (requestedRegistrationUri is not null)
        {
            if (string.IsNullOrWhiteSpace(requestedRegistrationUri))
                return new ErrorDetailResponse(RegistrationUriEmptyCode, ["registrationUri"]);
            if (!IsSipUri(requestedRegistrationUri))
                return new ErrorDetailResponse(InvalidSipUriCode, ["registrationUri"]);
        }

        if (!string.IsNullOrWhiteSpace(requestedClientUri) && !IsSipUri(requestedClientUri))
            return new ErrorDetailResponse(InvalidSipUriCode, ["clientUri"]);

        if (!string.IsNullOrWhiteSpace(registrationUri) && string.IsNullOrWhiteSpace(clientUri))
            return new ErrorDetailResponse(ClientUriRequiredCode, ["clientUri"]);

        return null;
    }

    /// <summary>
    /// True for <c>sip:</c> or <c>sips:</c> (any case) followed by at least one character and no whitespace or
    /// control character — the shape Asterisk accepts for <c>server_uri</c> / <c>client_uri</c>.
    /// </summary>
    private static bool IsSipUri(string value)
    {
        var rest = value.StartsWith("sips:", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(5)
            : value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(4)
            : [];
        if (rest.IsEmpty)
            return false;
        foreach (var c in rest)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        }
        return true;
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
