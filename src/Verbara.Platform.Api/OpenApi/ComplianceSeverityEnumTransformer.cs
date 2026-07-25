using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Verbara.Platform.Api.Endpoints;

namespace Verbara.Platform.Api.OpenApi;

/// <summary>
/// Narrows the emitted OpenAPI schema for <see cref="ComplianceRuleSummaryDto"/>'s
/// <c>severity</c> property from an open <c>string</c> to the closed literal enum
/// <c>Info | Warning | Critical</c>, so the emitted document declares the intended
/// contract rather than an unbounded string.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sibling of <see cref="NumericSchemaTruthTransformer"/> (openapi-residual-contract-shapes,
/// decision_ref Platform/ADR-0036).</b> Same <c>AddSchemaTransformer</c> seam, same
/// "make the emitted OpenAPI document tell the truth" posture. The two are orthogonal:
/// this one only touches the <c>severity</c> string property on the one named
/// <see cref="ComplianceRuleSummaryDto"/> schema; it never touches a numeric schema.
/// </para>
/// <para>
/// <b>Domain source (not invented here):</b> the three values <c>Info</c>, <c>Warning</c>,
/// <c>Critical</c> are exactly the members of the sibling <c>ComplianceSeverityBreakdownDto</c>
/// (<c>CallAnalyticsEndpoints.cs</c>), which enumerates the same producer's severity domain.
/// Pinned in <c>fixtures/compliance-rule-summary.v1.json</c>.
/// </para>
/// <para>
/// <b>Document-only, NO runtime/deserialization change:</b> the DTO member
/// <c>ComplianceRuleSummaryDto.Severity</c> stays <c>string</c> in source and stays registered
/// in <c>ApiJsonContext</c>. This transformer rewrites only the built <see cref="OpenApiSchema"/>
/// object model — the server still writes plain strings on the response and no request path binds
/// <c>severity</c> (it is a response-only field), so there is no untrusted deserialization path to
/// guard (design D1). Preserves the ADR-0036 "document states the truth, runtime stays as-is"
/// invariant verbatim.
/// </para>
/// <para>
/// <b>AOT-safe:</b> the transformer runs over the OpenAPI object model and identifies its target by
/// a compile-time <c>typeof</c> match against <see cref="OpenApiSchemaTransformerContext.JsonTypeInfo"/> —
/// no reflection over user types (ADR-0022).
/// </para>
/// <para>
/// <b>Lockstep rider:</b> if the producer ever adds a fourth severity, this enum AND the fixture
/// must be extended together — a spec-visible change, never a silent drift.
/// </para>
/// </remarks>
internal sealed class ComplianceSeverityEnumTransformer : IOpenApiSchemaTransformer
{
    /// <summary>The camelCase property name of <c>ComplianceRuleSummaryDto.Severity</c> in the emitted document.</summary>
    private const string SeverityPropertyName = "severity";

    /// <summary>The closed severity domain — mirrors <c>ComplianceSeverityBreakdownDto</c>'s three members.</summary>
    private static readonly string[] SeverityValues = ["Info", "Warning", "Critical"];

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        // Target only the ComplianceRuleSummaryDto object schema, by compile-time type identity
        // (AOT-safe — no reflection over user types). Any other schema is left untouched.
        if (context.JsonTypeInfo?.Type == typeof(ComplianceRuleSummaryDto))
        {
            NarrowSeverityToEnum(schema);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// If <paramref name="schema"/> declares a <c>severity</c> property, narrows it to a closed
    /// <c>string</c> enum <c>[Info, Warning, Critical]</c>. Idempotent — re-running over an
    /// already-narrowed schema produces the same result. Other properties are left untouched.
    /// </summary>
    internal static void NarrowSeverityToEnum(OpenApiSchema schema)
    {
        if (schema.Properties is not { } properties
            || !properties.TryGetValue(SeverityPropertyName, out IOpenApiSchema? severitySchema)
            || severitySchema is not OpenApiSchema severity)
        {
            return;
        }

        severity.Type = JsonSchemaType.String;
        severity.Enum = [.. SeverityValues.Select(v => (JsonNode)JsonValue.Create(v))];
    }
}
