using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Verbara.Platform.Api.OpenApi;

/// <summary>
/// Strips the spurious <c>string</c> arm that .NET 10's
/// <c>Microsoft.AspNetCore.OpenApi</c> + <c>JsonSchemaExporter</c> reflects onto
/// every numeric body/response schema, so the emitted OpenAPI document declares
/// numerics with a <b>single</b> JSON type (<c>integer</c>/<c>number</c>) instead
/// of the misleading <c>["integer","string"]</c>/<c>["number","string"]</c> union.
/// </summary>
/// <remarks>
/// <para>
/// <b>Root cause (artifact, not policy):</b> ASP.NET Core's framework-default
/// <c>JsonNumberHandling.AllowReadingFromString</c> is reflected into the exported
/// schema, so <see cref="OpenApiSchema.Type"/> gains the <see cref="JsonSchemaType.String"/>
/// flag alongside the numeric flag on every int32/int64/double/float field
/// (dotnet/aspnetcore #64145). The <i>document</i> lies — the server never writes
/// string-typed numbers on responses.
/// </para>
/// <para>
/// <b>Document-only, NOT <c>NumberHandling.Strict</c>:</b> this transformer rewrites
/// the built <see cref="OpenApiSchema"/> object model and does <b>not</b> touch
/// <c>JsonNumberHandling</c>. The serializer keeps <c>AllowReadingFromString</c>, so a
/// caller may still POST a quoted number (<c>"42"</c>) and it deserializes fine — the
/// contract states the canonical numeric form while the runtime stays lenient.
/// </para>
/// <para>
/// <b>AOT-safe:</b> transformers run over the OpenAPI object model; there is no
/// reflection over user types here (ADR-0022).
/// </para>
/// <para>
/// <b>Blanket over every numeric format with an EMPTY exemption list</b> (verified:
/// no Platform field approaches 2^53). ADR-0036 rider: any future field that genuinely
/// exceeds 2^53 MUST be modelled as an explicit <c>string</c> DTO property (so the
/// schema declares <c>type: string</c> deliberately), never reintroducing the union.
/// </para>
/// <para>
/// In Microsoft.OpenApi 2.9.0, <see cref="OpenApiSchema.Type"/> is a nullable
/// <see cref="JsonSchemaType"/> <c>[Flags]</c> enum (OpenAPI 3.1 shape); the union is a
/// bit-OR (e.g. <c>Integer | String</c>, or <c>Integer | String | Null</c> for a nullable
/// field). We clear only the <see cref="JsonSchemaType.String"/> bit, preserving the numeric
/// flag, the <see cref="JsonSchemaType.Null"/> (nullability) flag, <see cref="OpenApiSchema.Format"/>,
/// and everything else.
/// </para>
/// </remarks>
internal sealed class NumericSchemaTruthTransformer : IOpenApiSchemaTransformer
{
    /// <summary>The numeric type flags whose union with <see cref="JsonSchemaType.String"/> is the .NET 10 artifact.</summary>
    private const JsonSchemaType NumericFlags = JsonSchemaType.Integer | JsonSchemaType.Number;

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        RewriteNumericStringUnion(schema);
        return Task.CompletedTask;
    }

    /// <summary>
    /// If <paramref name="schema"/>'s <see cref="OpenApiSchema.Type"/> is a numeric+string
    /// union (a numeric flag set together with <see cref="JsonSchemaType.String"/>), clears the
    /// <see cref="JsonSchemaType.String"/> flag. A pure <c>string</c> schema (String set but no
    /// numeric flag) and a pure numeric schema (no String flag) are both left untouched.
    /// </summary>
    internal static void RewriteNumericStringUnion(OpenApiSchema schema)
    {
        JsonSchemaType? type = schema.Type;
        if (type is not { } flags)
        {
            return;
        }

        bool hasNumeric = (flags & NumericFlags) != 0;
        bool hasString = (flags & JsonSchemaType.String) != 0;

        // Only the numeric+string union is the artifact. Pure string, pure numeric,
        // and non-numeric composites (e.g. object/array) are all left as-is.
        if (hasNumeric && hasString)
        {
            schema.Type = flags & ~JsonSchemaType.String;
        }
    }
}
