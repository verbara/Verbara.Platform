using System.Collections.Frozen;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Verbara.Platform.Typification.Validation;
using Verbara.Sdk.Pro.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// Admin/designer surface for typification schemas and their scope bindings.
/// License-gated behind <see cref="LicenseFeature.AdvancedTypification"/>.
/// Published schema versions are immutable: editing a published version forks a
/// new draft version; draft versions are edited in place.
/// </summary>
internal static class TypificationEndpoints
{
    public static void MapTypificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/typification")
            .RequireAuthorization("AdminOnly")
            .RequireOperationalTenant()
            .RequireLicenseFeature(LicenseFeature.AdvancedTypification);

        // ── Schemas ──────────────────────────────────────────────────────────
        group.MapGet("/schemas", ListSchemas);
        group.MapGet("/schemas/{id}", GetSchema);
        group.MapPost("/schemas", CreateSchema);
        group.MapPut("/schemas/{id}", UpdateSchema);
        group.MapDelete("/schemas/{id}", DeleteSchema);
        group.MapPost("/schemas/{id}/publish", PublishSchema);
        group.MapGet("/schemas/{id}/calibration-status", GetCalibrationStatus);

        // ── Bindings ─────────────────────────────────────────────────────────
        group.MapGet("/bindings", ListBindings);
        group.MapGet("/bindings/{id}", GetBinding);
        group.MapPost("/bindings", CreateBinding);
        group.MapPut("/bindings/{id}", UpdateBinding);
        group.MapDelete("/bindings/{id}", DeleteBinding);

        // ── Autonomous-disposition activation gate (ADR-0034 Decision 3) ──────
        // A per-tenant CONTROLLER INSTRUCTION + config gate under the DPA — NOT the data
        // subject's consent. POST records the activation instruction (attesting admin id);
        // DELETE soft-revokes it. AdminOnly + AdvancedTypification (inherited from the group).
        group.MapPost("/autonomous-disposition", ActivateAutonomousDisposition);
        group.MapDelete("/autonomous-disposition", RevokeAutonomousDisposition);
    }

    // ─── Autonomous-disposition activation-gate handlers (ADR-0034) ───────────

    // POST /admin/typification/autonomous-disposition — record the activation instruction.
    // The attesting user id is the caller's (resolved in the canonical user_id ?? NameIdentifier
    // ?? sub order). On RE-activation we construct a FRESH, non-revoked record (B1 review): the
    // Upsert's EXCLUDED set copies revoked_at/revoked_by from the incoming record, so a stale
    // revoked record would leave a prior revocation in place. A fresh record clears it.
    private static async Task<IResult> ActivateAutonomousDisposition(
        HttpContext context,
        [FromServices] ITenantAutonomousDispositionStore store,
        [FromServices] IAuditService audit,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var callerUserId = GetCallerUserId(context);

        var record = new TenantAutonomousDisposition
        {
            TenantId = tenantId,
            AttestedByUserId = callerUserId,
            AttestedAt = clock.UtcNow,
            // FRESH record: revocation fields explicitly null so a prior revocation is cleared.
            RevokedAt = null,
            RevokedByUserId = null,
        };

        await store.UpsertAsync(record, ct);

        await RecordAudit(context, audit, tenantId, "typification.autonomous.gate_activated",
            targetId: tenantId.Value, before: null,
            after: new { AttestedByUserId = callerUserId }, ct);

        return Results.Created(
            "/admin/typification/autonomous-disposition",
            new AutonomousDispositionGateResponse(
                Active: true,
                AttestedByUserId: callerUserId,
                AttestedAt: record.AttestedAt,
                RevokedAt: null,
                RevokedByUserId: null));
    }

    // DELETE /admin/typification/autonomous-disposition — soft-revoke the gate.
    private static async Task<IResult> RevokeAutonomousDisposition(
        HttpContext context,
        [FromServices] ITenantAutonomousDispositionStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var callerUserId = GetCallerUserId(context);

        await store.RevokeAsync(tenantId, EntityId.From(callerUserId), ct);

        await RecordAudit(context, audit, tenantId, "typification.autonomous.gate_revoked",
            targetId: tenantId.Value, before: null,
            after: new { RevokedByUserId = callerUserId }, ct);

        return Results.NoContent();
    }

    // Caller user id via the shared canonical resolver (audit-trail-integrity-fixes, fix 3):
    // user_id (API-key owning user) ?? NameIdentifier ?? sub (JWT). Falls back to "system"
    // so the attestation always records a non-empty actor.
    private static string GetCallerUserId(HttpContext context) =>
        CallerIdentity.ResolveUserIdOrSystem(context.User);

    // ─── Schema handlers ─────────────────────────────────────────────────────

    private static async Task<IResult> ListSchemas(
        HttpContext context,
        [FromServices] ITypificationSchemaStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var schemas = await store.ListAsync(tenantId, ct);
        return Results.Ok(schemas.Select(ToSchemaDto).ToArray());
    }

    private static async Task<IResult> GetSchema(
        string id,
        HttpContext context,
        [FromServices] ITypificationSchemaStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var schema = await store.GetByIdAsync(tenantId, EntityId.From(id), version: null, ct);
        return schema is null ? Results.NotFound() : Results.Ok(ToSchemaDto(schema));
    }

    private static async Task<IResult> CreateSchema(
        HttpContext context,
        [FromBody] CreateSchemaRequest body,
        [FromServices] ITypificationSchemaStore store,
        [FromServices] IAuditService audit,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] ITypificationCalibration calibration,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        if (body.MaxDepth is < 1 or > 8)
            return Results.BadRequest(new PublishResultDto(
                Ok: false,
                Errors: [new PublishErrorDto("maxDepth", "MaxDepth must be between 1 and 8.")]));

        var schema = new TypificationSchema
        {
            SchemaId = EntityId.New(),
            TenantId = tenantId,
            Name = body.Name,
            Version = 1,
            IsPublished = false,
            MaxDepth = body.MaxDepth,
            Nodes = MapNodes(body.Nodes),
            Fields = MapFields(body.Fields),
            DataDips = [],
            // Create: empty EntityFieldMap (no prior schema). Omitted AiConfig → disabled default.
            AiConfig = MapAiConfig(body.AiConfig, new Dictionary<string, string>()),
            CreatedAt = clock.UtcNow,
            UpdatedAt = null,
        };

        // C4 — fail-fast write-time guards (autonomous permission + AutoFill calibration).
        // The schema id is brand-new, so an AutoFill request always rejects (no published
        // calibration yet) — by design: publish in Shadow/SuggestOnly first.
        var aiGate = await ValidateAiConfigWriteAsync(
            context, permissionResolver, calibration, tenantId, schema.SchemaId, schema.AiConfig, ct);
        if (aiGate is not null)
            return aiGate;

        await store.SaveAsync(schema, ct);
        await RecordAudit(context, audit, tenantId, "typification.schema.created",
            targetId: schema.SchemaId.Value, before: null,
            after: new { schema.SchemaId, schema.Name, schema.Version }, ct);

        return Results.Created($"/admin/typification/schemas/{schema.SchemaId}", ToSchemaDto(schema));
    }

    private static async Task<IResult> UpdateSchema(
        string id,
        HttpContext context,
        [FromBody] UpdateSchemaRequest body,
        [FromServices] ITypificationSchemaStore store,
        [FromServices] IAuditService audit,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] ITypificationCalibration calibration,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var schemaId = EntityId.From(id);
        var latest = await store.GetByIdAsync(tenantId, schemaId, version: null, ct);
        if (latest is null)
            return Results.NotFound();

        if (body.MaxDepth is < 1 or > 8)
            return Results.BadRequest(new PublishResultDto(
                Ok: false,
                Errors: [new PublishErrorDto("maxDepth", "MaxDepth must be between 1 and 8.")]));

        // Versioning rule: published versions are immutable — fork a new draft;
        // an unpublished draft is edited in place (version preserved).
        var nextVersion = latest.IsPublished ? latest.Version + 1 : latest.Version;

        var updated = new TypificationSchema
        {
            SchemaId = schemaId,
            TenantId = tenantId,
            Name = body.Name,
            Version = nextVersion,
            IsPublished = false,
            MaxDepth = body.MaxDepth,
            Nodes = MapNodes(body.Nodes),
            Fields = MapFields(body.Fields),
            DataDips = latest.DataDips,
            // Update: preserve the prior schema's EntityFieldMap (P2b owns map editing).
            // Omitted AiConfig → disabled default (still carrying the existing map forward).
            AiConfig = MapAiConfig(body.AiConfig, latest.AiConfig.EntityFieldMap),
            CreatedAt = latest.CreatedAt,
            UpdatedAt = clock.UtcNow,
        };

        // C4 — fail-fast write-time guards (autonomous permission + AutoFill calibration)
        // against the EXISTING schema id, before persisting. Rejecting here means no save
        // and no audit entry.
        var aiGate = await ValidateAiConfigWriteAsync(
            context, permissionResolver, calibration, tenantId, schemaId, updated.AiConfig, ct);
        if (aiGate is not null)
            return aiGate;

        await store.SaveAsync(updated, ct);
        await RecordAudit(context, audit, tenantId, "typification.schema.updated",
            targetId: schemaId.Value,
            before: new { latest.SchemaId, latest.Version, latest.IsPublished },
            after: new { updated.SchemaId, updated.Version, updated.IsPublished }, ct);

        return Results.Ok(ToSchemaDto(updated));
    }

    private static async Task<IResult> DeleteSchema(
        string id,
        HttpContext context,
        [FromServices] ITypificationSchemaStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var schemaId = EntityId.From(id);
        var before = await store.GetByIdAsync(tenantId, schemaId, version: null, ct);
        await store.DeleteAsync(tenantId, schemaId, ct);
        await RecordAudit(context, audit, tenantId, "typification.schema.deleted",
            targetId: id,
            before: before is null ? null : new { before.SchemaId, before.Name },
            after: null, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> PublishSchema(
        string id,
        HttpContext context,
        [FromServices] ITypificationSchemaStore store,
        [FromServices] ITypificationValidator validator,
        [FromServices] IAuditService audit,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var schemaId = EntityId.From(id);
        var latest = await store.GetByIdAsync(tenantId, schemaId, version: null, ct);
        if (latest is null)
            return Results.NotFound();

        var validation = validator.ValidateForPublish(latest);
        if (!validation.IsValid)
        {
            return Results.Ok(new PublishResultDto(
                Ok: false,
                Errors: validation.Errors.Select(e => new PublishErrorDto(e.Field, e.Message)).ToArray()));
        }

        var published = latest with { IsPublished = true, UpdatedAt = clock.UtcNow };
        await store.SaveAsync(published, ct);
        await RecordAudit(context, audit, tenantId, "typification.schema.published",
            targetId: schemaId.Value,
            before: new { latest.SchemaId, latest.Version, latest.IsPublished },
            after: new { published.SchemaId, published.Version, published.IsPublished }, ct);

        return Results.Ok(new PublishResultDto(Ok: true, Errors: []));
    }

    // C4 — calibration snapshot for the AI-config admin surface. Reads the empirical
    // readiness (sample count, accuracy, AutoFill/autonomous gates) so the designer UI
    // can show whether AutoFill / autonomous can yet be enabled. Requires the
    // `typification:ai:configure` permission (same audience that edits AiConfig).
    private static async Task<IResult> GetCalibrationStatus(
        string id,
        HttpContext context,
        [FromServices] ITypificationCalibration calibration,
        [FromServices] PermissionResolver permissionResolver,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        var perms = await ResolveCallerPermissions(context, permissionResolver, tenantId, ct);
        if (!PermissionResolver.HasPermission(perms, "typification:ai:configure"))
            return Results.Forbid();

        // GetStatusAsync returns an all-zero / not-ready snapshot for a missing or
        // unpublished schema — that IS the correct "not calibrated" answer, so there
        // is no NotFound special-casing here.
        var status = await calibration.GetStatusAsync(tenantId, EntityId.From(id), ct);
        return Results.Ok(new CalibrationStatusDto(
            status.Samples, status.Accuracy, status.AutoFillReady, status.AutonomousReady));
    }

    // ─── Binding handlers ────────────────────────────────────────────────────

    private static async Task<IResult> ListBindings(
        HttpContext context,
        [FromServices] ISchemaBindingStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var bindings = await store.ListAsync(tenantId, ct);
        return Results.Ok(bindings.Select(ToBindingDto).ToArray());
    }

    private static async Task<IResult> GetBinding(
        string id,
        HttpContext context,
        [FromServices] ISchemaBindingStore store,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var binding = await store.GetByIdAsync(tenantId, EntityId.From(id), ct);
        return binding is null ? Results.NotFound() : Results.Ok(ToBindingDto(binding));
    }

    private static async Task<IResult> CreateBinding(
        HttpContext context,
        [FromBody] CreateBindingRequest body,
        [FromServices] ISchemaBindingStore store,
        [FromServices] IAuditService audit,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] ITypificationCalibration calibration,
        IClock clock,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);

        if (!Enum.TryParse<BindingScope>(body.Scope, ignoreCase: true, out var scope))
            return Results.BadRequest(new PublishResultDto(
                Ok: false,
                Errors: [new PublishErrorDto("scope", $"Unknown binding scope '{body.Scope}'.")]));

        if (scope != BindingScope.Tenant && string.IsNullOrWhiteSpace(body.ScopeRef))
            return Results.BadRequest(new PublishResultDto(
                Ok: false,
                Errors: [new PublishErrorDto("scopeRef", $"ScopeRef is required for scope '{scope}'.")]));

        var aiConfigOverride = MapBindingAiConfigOverride(body.AiConfigOverride);

        // E1 — close the gate-bypass: a per-binding AI override is subject to the SAME write-time
        // guards as a schema's AiConfig (autonomous permission + AutoFill calibration). Calibration
        // keys on the override's target schema (body.SchemaId) — the schema this override pilots.
        // Skipped entirely when there is no override (no behavior change for non-AI bindings); the
        // gate runs BEFORE persist/audit so a rejection neither saves nor records the binding.
        if (aiConfigOverride is not null)
        {
            var gate = await ValidateAiConfigWriteAsync(
                context, permissionResolver, calibration, tenantId, EntityId.From(body.SchemaId), aiConfigOverride, ct);
            if (gate is not null)
                return gate;
        }

        var binding = new SchemaBinding
        {
            BindingId = EntityId.New(),
            TenantId = tenantId,
            Scope = scope,
            ScopeRef = body.ScopeRef,
            SchemaId = EntityId.From(body.SchemaId),
            SubTreeRootNodeId = body.SubtreeRootNodeId is { Length: > 0 } s ? EntityId.From(s) : null,
            Priority = body.Priority,
            AiConfigOverride = aiConfigOverride,
            CreatedAt = clock.UtcNow,
        };

        await store.SaveAsync(binding, ct);
        await RecordAudit(context, audit, tenantId, "typification.binding.created",
            targetId: binding.BindingId.Value, before: null,
            after: new { binding.BindingId, binding.Scope, binding.SchemaId }, ct);

        return Results.Created($"/admin/typification/bindings/{binding.BindingId}", ToBindingDto(binding));
    }

    private static async Task<IResult> UpdateBinding(
        string id,
        HttpContext context,
        [FromBody] UpdateBindingRequest body,
        [FromServices] ISchemaBindingStore store,
        [FromServices] IAuditService audit,
        [FromServices] PermissionResolver permissionResolver,
        [FromServices] ITypificationCalibration calibration,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var bindingId = EntityId.From(id);
        var existing = await store.GetByIdAsync(tenantId, bindingId, ct);
        if (existing is null)
            return Results.NotFound();

        if (!Enum.TryParse<BindingScope>(body.Scope, ignoreCase: true, out var scope))
            return Results.BadRequest(new PublishResultDto(
                Ok: false,
                Errors: [new PublishErrorDto("scope", $"Unknown binding scope '{body.Scope}'.")]));

        if (scope != BindingScope.Tenant && string.IsNullOrWhiteSpace(body.ScopeRef))
            return Results.BadRequest(new PublishResultDto(
                Ok: false,
                Errors: [new PublishErrorDto("scopeRef", $"ScopeRef is required for scope '{scope}'.")]));

        var aiConfigOverride = MapBindingAiConfigOverride(body.AiConfigOverride);

        // E1 — close the gate-bypass: a per-binding AI override is subject to the SAME write-time
        // guards as a schema's AiConfig (autonomous permission + AutoFill calibration). Calibration
        // keys on the override's target schema (body.SchemaId). Skipped entirely when there is no
        // override; runs BEFORE persist/audit so a rejection neither saves nor records the binding.
        if (aiConfigOverride is not null)
        {
            var gate = await ValidateAiConfigWriteAsync(
                context, permissionResolver, calibration, tenantId, EntityId.From(body.SchemaId), aiConfigOverride, ct);
            if (gate is not null)
                return gate;
        }

        var updated = new SchemaBinding
        {
            BindingId = bindingId,
            TenantId = tenantId,
            Scope = scope,
            ScopeRef = body.ScopeRef,
            SchemaId = EntityId.From(body.SchemaId),
            SubTreeRootNodeId = body.SubtreeRootNodeId is { Length: > 0 } s ? EntityId.From(s) : null,
            Priority = body.Priority,
            AiConfigOverride = aiConfigOverride,
            CreatedAt = existing.CreatedAt,
        };

        await store.SaveAsync(updated, ct);
        await RecordAudit(context, audit, tenantId, "typification.binding.updated",
            targetId: bindingId.Value,
            before: new { existing.BindingId, existing.Scope, existing.SchemaId },
            after: new { updated.BindingId, updated.Scope, updated.SchemaId }, ct);

        return Results.Ok(ToBindingDto(updated));
    }

    private static async Task<IResult> DeleteBinding(
        string id,
        HttpContext context,
        [FromServices] ISchemaBindingStore store,
        [FromServices] IAuditService audit,
        CancellationToken ct)
    {
        var tenantId = GetTenantId(context);
        var bindingId = EntityId.From(id);
        var before = await store.GetByIdAsync(tenantId, bindingId, ct);
        await store.DeleteAsync(tenantId, bindingId, ct);
        await RecordAudit(context, audit, tenantId, "typification.binding.deleted",
            targetId: id,
            before: before is null ? null : new { before.BindingId, before.Scope },
            after: null, ct);
        return Results.NoContent();
    }

    // ─── DTO ↔ domain mapping (fully explicit, reflection-free) ───────────────

    internal static TypificationSchemaDto ToSchemaDto(TypificationSchema s) =>
        new(
            SchemaId: s.SchemaId.Value,
            Name: s.Name,
            Version: s.Version,
            IsPublished: s.IsPublished,
            MaxDepth: s.MaxDepth,
            Nodes: s.Nodes.Select(ToNodeDto).ToArray(),
            Fields: s.Fields.Select(ToFieldDto).ToArray(),
            AiConfig: ToAiConfigDto(s.AiConfig),
            CreatedAt: s.CreatedAt,
            UpdatedAt: s.UpdatedAt);

    // P2b — AiConfig round-trip with graduated bands.
    // D4 — EntityFieldMap (AI entity → field Key) and PiiAllowStore (allow-listed PiiType
    // names) are now emitted on the wire. PiiAllowStore is rendered in deterministic ordinal
    // order for stable round-trips/tests; PiiPolicy is non-null in the domain (defaulted
    // DenyAll), coalesced defensively here.
    private static AiConfigDto ToAiConfigDto(TypificationAiConfig c) =>
        new(
            Enabled: c.Enabled,
            Mode: c.Mode.ToString(),
            SuggestThreshold: c.SuggestThreshold,
            AutoApplyThreshold: c.AutoApplyThreshold,
            AutonomousThreshold: c.AutonomousThreshold,
            Autonomous: c.Autonomous,
            SentimentGating: c.SentimentGating,
            DailyTokenBudget: c.DailyTokenBudget,
            EntityFieldMap: c.EntityFieldMap,
            PiiAllowStore: (c.PiiPolicy ?? PiiPolicy.DenyAll).AllowStore
                .Select(t => t.ToString())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray());

    private static TypificationNodeDto ToNodeDto(TypificationNode n) =>
        new(
            NodeId: n.NodeId.Value,
            ParentNodeId: n.ParentNodeId?.Value,
            Label: n.Label,
            Code: n.Code,
            SortOrder: n.SortOrder,
            IsLeaf: n.IsLeaf,
            ChannelApplicability: n.ChannelApplicability?.Select(c => c.ToString()).ToArray(),
            Leaf: n.Leaf is null ? null : ToLeafDto(n.Leaf));

    private static LeafOutcomeDto ToLeafDto(LeafOutcome l) =>
        new(
            Category: l.Category.ToString(),
            TriggerRetry: l.TriggerRetry,
            RetryDelayMinutes: l.RetryDelayMinutes,
            TriggerCallback: l.TriggerCallback,
            DialerCode: l.DialerCode,
            IsActive: l.IsActive);

    private static TypificationFieldDto ToFieldDto(TypificationField f) =>
        new(
            FieldId: f.FieldId.Value,
            Key: f.Key,
            Label: f.Label,
            Type: f.Type.ToString(),
            Required: f.Required,
            Options: f.Options?.Select(o => new FieldOptionDto(o.Value, o.Label)).ToArray(),
            Validation: f.Validation is null
                ? null
                : new FieldValidationDto(f.Validation.Regex, f.Validation.Min, f.Validation.Max, f.Validation.MaxLength),
            AttachToNodeId: f.AttachToNodeId?.Value,
            VisibleWhen: f.VisibleWhen is null
                ? null
                : new ConditionExprDto(f.VisibleWhen.RefType.ToString(), f.VisibleWhen.Ref, f.VisibleWhen.Op.ToString(), f.VisibleWhen.Value),
            PrefillSource: f.PrefillSource is { } pr
                ? new PrefillSourceDto(pr.Kind.ToString(), pr.Ref)
                : null,
            SortOrder: f.SortOrder);

    private static SchemaBindingDto ToBindingDto(SchemaBinding b) =>
        new(
            BindingId: b.BindingId.Value,
            Scope: b.Scope.ToString(),
            ScopeRef: b.ScopeRef,
            SchemaId: b.SchemaId.Value,
            SubtreeRootNodeId: b.SubTreeRootNodeId?.Value,
            Priority: b.Priority,
            // E1 — emit the override only when present (null = inherit the schema's AiConfig).
            AiConfigOverride: b.AiConfigOverride is null ? null : ToAiConfigDto(b.AiConfigOverride));

    private static List<TypificationNode> MapNodes(IReadOnlyList<TypificationNodeDto> dtos) =>
        dtos.Select(n => new TypificationNode
        {
            NodeId = n.NodeId is { Length: > 0 } ? EntityId.From(n.NodeId) : EntityId.New(),
            ParentNodeId = n.ParentNodeId is { Length: > 0 } p ? EntityId.From(p) : null,
            Label = n.Label,
            Code = n.Code,
            SortOrder = n.SortOrder,
            IsLeaf = n.IsLeaf,
            ChannelApplicability = n.ChannelApplicability is null
                ? null
                : n.ChannelApplicability.Select(c => Enum.Parse<ChannelType>(c, ignoreCase: true)).ToList(),
            Leaf = n.Leaf is null ? null : MapLeaf(n.Leaf),
        }).ToList();

    private static LeafOutcome MapLeaf(LeafOutcomeDto l) =>
        new()
        {
            Category = Enum.Parse<TypificationCategory>(l.Category, ignoreCase: true),
            TriggerRetry = l.TriggerRetry,
            RetryDelayMinutes = l.RetryDelayMinutes,
            TriggerCallback = l.TriggerCallback,
            DialerCode = l.DialerCode,
            IsActive = l.IsActive,
        };

    private static List<TypificationField> MapFields(IReadOnlyList<TypificationFieldDto> dtos) =>
        dtos.Select(f => new TypificationField
        {
            FieldId = f.FieldId is { Length: > 0 } ? EntityId.From(f.FieldId) : EntityId.New(),
            Key = f.Key,
            Label = f.Label,
            Type = Enum.Parse<FieldType>(f.Type, ignoreCase: true),
            Required = f.Required,
            Options = f.Options is null
                ? null
                : f.Options.Select(o => new FieldOption { Value = o.Value, Label = o.Label }).ToList(),
            Validation = f.Validation is null
                ? null
                : new FieldValidation
                {
                    Regex = f.Validation.Regex,
                    Min = f.Validation.Min,
                    Max = f.Validation.Max,
                    MaxLength = f.Validation.MaxLength,
                },
            AttachToNodeId = f.AttachToNodeId is { Length: > 0 } a ? EntityId.From(a) : null,
            VisibleWhen = f.VisibleWhen is null
                ? null
                : new ConditionExpr
                {
                    RefType = Enum.Parse<ConditionRef>(f.VisibleWhen.RefType, ignoreCase: true),
                    Ref = f.VisibleWhen.Ref,
                    Op = Enum.Parse<ConditionOp>(f.VisibleWhen.Op, ignoreCase: true),
                    Value = f.VisibleWhen.Value,
                },
            PrefillSource = MapPrefillSource(f.PrefillSource),
            SortOrder = f.SortOrder,
        }).ToList();

    // PrefillSource is tolerant on input (unlike the throwing VisibleWhen enum maps):
    // an unknown Kind or an empty Ref maps to null rather than failing the request,
    // since prefill is an optional P2/P3 capability layered onto the schema.
    private static PrefillRef? MapPrefillSource(PrefillSourceDto? dto)
    {
        if (dto is not { } ps || string.IsNullOrWhiteSpace(ps.Ref))
            return null;

        return Enum.TryParse<PrefillSourceKind>(ps.Kind, ignoreCase: true, out var kind)
            ? new PrefillRef { Kind = kind, Ref = ps.Ref }
            : null;
    }

    private static TypificationAiConfig EmptyAiConfig() =>
        new()
        {
            Enabled = false,
            Mode = AiMode.Off,
            // Threshold defaults come from the record property initialisers.
            SentimentGating = true,
            EntityFieldMap = new Dictionary<string, string>(),
        };

    // P2b — build the domain AiConfig from the optional request DTO. An omitted DTO yields
    // the disabled default (preserving the existing EntityFieldMap). Unknown Mode strings fall
    // back to Off (tolerant — AI config is optional layered metadata). Thresholds are clamped
    // to [0, 1]: a value > 1 suppresses everything; < 0 never suppresses; both outcomes would
    // be confusing ops mistakes rather than intent.
    // D4 — the wire is now authoritative for EntityFieldMap + PiiAllowStore, with a DELIBERATE
    // asymmetry on omission:
    //  • EntityFieldMap: a provided map wins; an OMITTED map PRESERVES the existing one
    //    (`?? existingEntityFieldMap`).
    //  • PiiAllowStore:  a provided list parses to a PiiPolicy; an OMITTED list RESETS the
    //    policy to DenyAll (ParsePiiPolicy(null) → DenyAll) — it does NOT preserve.
    // The reset is safe BY DESIGN: DenyAll is the restrictive/fail-closed direction, so a reset
    // can only re-mask PII, never silently leak it. Callers that own the policy (the Web schema
    // designer, D4-web) MUST always send piiAllowStore on every write so an edit elsewhere in
    // the config never accidentally un-allow-lists a tenant's permitted PII types.
    private static TypificationAiConfig MapAiConfig(
        AiConfigDto? dto, IReadOnlyDictionary<string, string> existingEntityFieldMap)
    {
        if (dto is null)
            return EmptyAiConfig() with { EntityFieldMap = existingEntityFieldMap };

        return new TypificationAiConfig
        {
            Enabled = dto.Enabled,
            Mode = Enum.TryParse<AiMode>(dto.Mode, ignoreCase: true, out var m) ? m : AiMode.Off,
            SuggestThreshold = Math.Clamp(dto.SuggestThreshold, 0.0, 1.0),
            AutoApplyThreshold = Math.Clamp(dto.AutoApplyThreshold, 0.0, 1.0),
            AutonomousThreshold = Math.Clamp(dto.AutonomousThreshold, 0.0, 1.0),
            Autonomous = dto.Autonomous,
            SentimentGating = dto.SentimentGating,
            DailyTokenBudget = dto.DailyTokenBudget,
            EntityFieldMap = dto.EntityFieldMap ?? existingEntityFieldMap,
            PiiPolicy = ParsePiiPolicy(dto.PiiAllowStore),
        };
    }

    // E1 — map an optional per-binding AI config OVERRIDE DTO to the domain. A null DTO yields
    // null (the binding inherits the schema's AiConfig). Unlike a schema's AiConfig, a binding
    // override carries its OWN EntityFieldMap on the wire (there is no schema-map to preserve for
    // an override), so the override DTO's own map is passed as the "existing" map to MapAiConfig:
    // a provided map wins; an omitted map collapses to empty (no prior binding-override map to
    // carry forward). PiiAllowStore follows MapAiConfig's reset-to-DenyAll-on-omission semantics.
    private static TypificationAiConfig? MapBindingAiConfigOverride(AiConfigDto? dto) =>
        dto is null
            ? null
            : MapAiConfig(dto, dto.EntityFieldMap ?? new Dictionary<string, string>());

    // D4 — map the wire PiiAllowStore (PiiType member names) to a domain PiiPolicy. A null
    // list → the canonical DenyAll singleton. Names are parsed case-insensitively (AOT-safe
    // Enum.TryParse); unknown names are tolerantly skipped (consistent with the other tolerant
    // parses in this file). An empty / all-unknown list also collapses to DenyAll. A non-empty
    // result is backed by a FrozenSet so the domain holds an immutable, fast-Contains policy —
    // never a mutable HashSet behind IReadOnlySet (no down-cast-and-mutate hole), and its empty
    // shape matches the canonical DenyAll (FrozenSet<PiiType>.Empty). Mirrors
    // PiiPolicyJsonConverter.Read, which also frozen-backs the parsed set.
    private static PiiPolicy ParsePiiPolicy(IReadOnlyList<string>? names)
    {
        if (names is null)
            return PiiPolicy.DenyAll;

        var set = new HashSet<PiiType>();
        foreach (var name in names)
        {
            if (Enum.TryParse<PiiType>(name, ignoreCase: true, out var t))
                set.Add(t);
        }

        return set.Count == 0
            ? PiiPolicy.DenyAll
            : new PiiPolicy { AllowStore = set.ToFrozenSet() };
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    // audit-trail-integrity-fixes (fix 3): actorId is resolved via the shared canonical
    // resolver (user_id ?? NameIdentifier ?? sub), NOT a `sub`-only lookup. A `sub`-only lookup
    // records API-key callers (no `sub` claim) as "system" and impersonated sessions as the
    // impersonated subject rather than the resolved caller — the same claim-order bug class
    // fixed for rate limiting / impersonation in v2.14.1.
    private static Task RecordAudit(
        HttpContext context,
        IAuditService audit,
        TenantId tenantId,
        string action,
        string targetId,
        object? before,
        object? after,
        CancellationToken ct) =>
        audit.RecordAsync(
            tenantId, category: "config", action: action, severity: "info",
            actorId: CallerIdentity.ResolveUserIdOrSystem(context.User), actorType: "user",
            targetId: targetId, targetType: "typification",
            changes: new AuditChanges(Before: before, After: after),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);

    private static TenantId GetTenantId(HttpContext context)
    {
        if (context.Items.TryGetValue("TenantId", out var val) && val is TenantId tid)
            return tid;

        throw new InvalidOperationException("Tenant ID not resolved");
    }

    // C4 — resolve the caller's effective permissions for the current tenant. The caller
    // user id is resolved in the canonical order used by PermissionAuthorizationHandler and
    // PlatformAdminAuthorizationHandler: `user_id` ?? ClaimTypes.NameIdentifier ?? `sub`.
    // For API-key callers `user_id` is the owning-user claim while NameIdentifier is the
    // key id — so `user_id` MUST win, otherwise the key id resolves to an empty permission
    // set and every gate 403s even for authorized callers. JWT callers carry only `sub`.
    // A caller with no resolvable id yields an empty permission set (so every gate fails closed).
    private static async Task<IReadOnlySet<string>> ResolveCallerPermissions(
        HttpContext context,
        PermissionResolver permissionResolver,
        TenantId tenantId,
        CancellationToken ct)
    {
        var callerUserId = CallerIdentity.ResolveUserId(context.User);

        if (string.IsNullOrEmpty(callerUserId))
            return new HashSet<string>(StringComparer.Ordinal);

        return await permissionResolver.ResolveAsync(tenantId, EntityId.From(callerUserId), ct);
    }

    // C4 — defense-in-depth write-time AI-config validation (Create + Update). Returns a
    // non-null IResult (the error response) when the write must be rejected; null when OK.
    private static async Task<IResult?> ValidateAiConfigWriteAsync(
        HttpContext context,
        PermissionResolver permissionResolver,
        ITypificationCalibration calibration,
        TenantId tenantId,
        EntityId schemaId,
        TypificationAiConfig aiConfig,
        CancellationToken ct)
    {
        // Autonomous disposition is the highest-risk setting → stricter permission.
        if (aiConfig.Autonomous)
        {
            var perms = await ResolveCallerPermissions(context, permissionResolver, tenantId, ct);
            if (!PermissionResolver.HasPermission(perms, "typification:ai:autonomous"))
                return Results.Forbid();
        }

        // AutoFill must be earned: reject enabling it on a schema whose published
        // version hasn't reached the calibration bar (mirrors the runtime band gate).
        if (aiConfig.Mode == AiMode.AutoFill)
        {
            var status = await calibration.GetStatusAsync(tenantId, schemaId, ct);
            if (!status.AutoFillReady)
                return Results.BadRequest(new PublishResultDto(
                    Ok: false,
                    Errors: [new PublishErrorDto("aiConfig.mode",
                        "AutoFill requires the schema to reach the calibration bar (samples + accuracy). Run in Shadow/SuggestOnly until calibration is ready.")]));
        }

        return null;
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record TypificationSchemaDto(
    string SchemaId,
    string Name,
    int Version,
    bool IsPublished,
    int MaxDepth,
    IReadOnlyList<TypificationNodeDto> Nodes,
    IReadOnlyList<TypificationFieldDto> Fields,
    AiConfigDto AiConfig,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

// P2b — AI auto-disposition config with graduated bands.
// D4 (P2b) — EntityFieldMap (AI entity name → field Key) and PiiAllowStore (the PiiType
// member NAMES the tenant has allow-listed, e.g. ["Email","Phone"]) are now editable on the
// wire so the Web schema designer can manage them. Both are nullable: an omitting client
// preserves the existing EntityFieldMap and defaults PiiAllowStore to DenyAll (empty), the
// same robustness pattern as the rest of the config. PiiAllowStore is a names array (not the
// PiiPolicy object / PiiType enum) to keep the wire simple and the PiiPolicyJsonConverter
// out of the API surface; MapAiConfig maps names ↔ PiiPolicy.
internal sealed record AiConfigDto(
    bool Enabled,
    string Mode,
    double SuggestThreshold,
    double AutoApplyThreshold,
    double AutonomousThreshold,
    bool Autonomous,
    bool SentimentGating,
    long? DailyTokenBudget,
    IReadOnlyDictionary<string, string>? EntityFieldMap = null,
    IReadOnlyList<string>? PiiAllowStore = null);

internal sealed record TypificationNodeDto(
    string NodeId,
    string? ParentNodeId,
    string Label,
    string Code,
    int SortOrder,
    bool IsLeaf,
    IReadOnlyList<string>? ChannelApplicability,
    LeafOutcomeDto? Leaf);

internal sealed record LeafOutcomeDto(
    string Category,
    bool TriggerRetry,
    int? RetryDelayMinutes,
    bool TriggerCallback,
    string? DialerCode,
    bool IsActive);

internal sealed record TypificationFieldDto(
    string FieldId,
    string Key,
    string Label,
    string Type,
    bool Required,
    IReadOnlyList<FieldOptionDto>? Options,
    FieldValidationDto? Validation,
    string? AttachToNodeId,
    ConditionExprDto? VisibleWhen,
    PrefillSourceDto? PrefillSource,
    int SortOrder);

internal sealed record FieldOptionDto(string Value, string Label);

internal sealed record FieldValidationDto(
    string? Regex,
    double? Min,
    double? Max,
    int? MaxLength);

internal sealed record ConditionExprDto(
    string RefType,
    string Ref,
    string Op,
    string? Value);

internal sealed record PrefillSourceDto(string Kind, string Ref);

internal sealed record SchemaBindingDto(
    string BindingId,
    string Scope,
    string? ScopeRef,
    string SchemaId,
    string? SubtreeRootNodeId,
    int Priority,
    // E1 — optional per-binding AI config override (null = inherit the schema's AiConfig).
    // Carries its OWN EntityFieldMap on the wire; the effective config resolved at runtime is
    // `AiConfigOverride ?? schema.AiConfig`.
    AiConfigDto? AiConfigOverride = null);

// ─── Request DTOs ───────────────────────────────────────────────────────────

internal sealed record CreateSchemaRequest(
    string Name,
    int MaxDepth,
    IReadOnlyList<TypificationNodeDto> Nodes,
    IReadOnlyList<TypificationFieldDto> Fields,
    AiConfigDto? AiConfig = null);

internal sealed record UpdateSchemaRequest(
    string Name,
    int MaxDepth,
    IReadOnlyList<TypificationNodeDto> Nodes,
    IReadOnlyList<TypificationFieldDto> Fields,
    AiConfigDto? AiConfig = null);

internal sealed record CreateBindingRequest(
    string Scope,
    string? ScopeRef,
    string SchemaId,
    string? SubtreeRootNodeId,
    int Priority,
    // E1 — optional per-binding AI config override (null = inherit the schema's AiConfig).
    AiConfigDto? AiConfigOverride = null);

internal sealed record UpdateBindingRequest(
    string Scope,
    string? ScopeRef,
    string SchemaId,
    string? SubtreeRootNodeId,
    int Priority,
    // E1 — optional per-binding AI config override (null = inherit the schema's AiConfig).
    AiConfigDto? AiConfigOverride = null);

internal sealed record PublishResultDto(bool Ok, IReadOnlyList<PublishErrorDto> Errors);

internal sealed record PublishErrorDto(string Field, string Message);

// C4 — calibration snapshot DTO for GET /schemas/{id}/calibration-status.
internal sealed record CalibrationStatusDto(
    int Samples,
    double Accuracy,
    bool AutoFillReady,
    bool AutonomousReady);

// ─── ADR-0034 — autonomous-disposition activation-gate DTOs ────────────────────

/// <summary>
/// Response describing the per-tenant autonomous-disposition activation gate (ADR-0034 Decision 3):
/// whether it is currently active and the controller-instruction attestation/revocation metadata.
/// </summary>
internal sealed record AutonomousDispositionGateResponse(
    bool Active,
    string AttestedByUserId,
    DateTimeOffset AttestedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedByUserId);
