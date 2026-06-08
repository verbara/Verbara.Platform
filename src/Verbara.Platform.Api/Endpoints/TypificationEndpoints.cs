using Verbara.Platform.Api.Middleware;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
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

        // ── Bindings ─────────────────────────────────────────────────────────
        group.MapGet("/bindings", ListBindings);
        group.MapGet("/bindings/{id}", GetBinding);
        group.MapPost("/bindings", CreateBinding);
        group.MapPut("/bindings/{id}", UpdateBinding);
        group.MapDelete("/bindings/{id}", DeleteBinding);
    }

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
            AiConfig = EmptyAiConfig(),
            CreatedAt = clock.UtcNow,
            UpdatedAt = null,
        };

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
            AiConfig = latest.AiConfig,
            CreatedAt = latest.CreatedAt,
            UpdatedAt = clock.UtcNow,
        };

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

        var binding = new SchemaBinding
        {
            BindingId = EntityId.New(),
            TenantId = tenantId,
            Scope = scope,
            ScopeRef = body.ScopeRef,
            SchemaId = EntityId.From(body.SchemaId),
            SubTreeRootNodeId = body.SubtreeRootNodeId is { Length: > 0 } s ? EntityId.From(s) : null,
            Priority = body.Priority,
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

        var updated = new SchemaBinding
        {
            BindingId = bindingId,
            TenantId = tenantId,
            Scope = scope,
            ScopeRef = body.ScopeRef,
            SchemaId = EntityId.From(body.SchemaId),
            SubTreeRootNodeId = body.SubtreeRootNodeId is { Length: > 0 } s ? EntityId.From(s) : null,
            Priority = body.Priority,
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
            CreatedAt: s.CreatedAt,
            UpdatedAt: s.UpdatedAt);

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
            Priority: b.Priority);

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
            Mode = default,
            ConfidenceThreshold = 0,
            SentimentGating = false,
            EntityFieldMap = new Dictionary<string, string>(),
        };

    // ─── Helpers ──────────────────────────────────────────────────────────────

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
            actorId: context.User.FindFirst("sub")?.Value ?? "system", actorType: "user",
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
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

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
    int Priority);

// ─── Request DTOs ───────────────────────────────────────────────────────────

internal sealed record CreateSchemaRequest(
    string Name,
    int MaxDepth,
    IReadOnlyList<TypificationNodeDto> Nodes,
    IReadOnlyList<TypificationFieldDto> Fields);

internal sealed record UpdateSchemaRequest(
    string Name,
    int MaxDepth,
    IReadOnlyList<TypificationNodeDto> Nodes,
    IReadOnlyList<TypificationFieldDto> Fields);

internal sealed record CreateBindingRequest(
    string Scope,
    string? ScopeRef,
    string SchemaId,
    string? SubtreeRootNodeId,
    int Priority);

internal sealed record UpdateBindingRequest(
    string Scope,
    string? ScopeRef,
    string SchemaId,
    string? SubtreeRootNodeId,
    int Priority);

internal sealed record PublishResultDto(bool Ok, IReadOnlyList<PublishErrorDto> Errors);

internal sealed record PublishErrorDto(string Field, string Message);
