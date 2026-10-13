using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Verbara.Platform.Api.Endpoints;

namespace Verbara.Platform.Api.OpenApi;

/// <summary>
/// Makes the emitted OpenAPI schemas of <see cref="VoiceDialResponse"/> and <see cref="VoiceTransferResponse"/> say
/// which members are always written (outbound-calls-reach-the-trunk, design D4). <see cref="Serialization.ApiJsonContext"/>
/// omits a null member (<c>WhenWritingNull</c>), so <c>correlationId</c> and <c>error</c> are optional, while
/// <c>trunkId</c> opts out (<c>JsonIgnoreCondition.Never</c>) and is required. The .NET 10 schema exporter instead lists
/// every constructor parameter without a default as required, and the defaulted <c>trunkId</c> as optional.
/// </summary>
/// <remarks>
/// Document-only, the posture of <see cref="NullableCredentialValuesTransformer"/>. AOT-safe: the targets are identified
/// by a compile-time <c>typeof</c> match, no reflection over user types.
/// </remarks>
internal sealed class VoiceResponseMembersTransformer : IOpenApiSchemaTransformer
{
    private const string AlwaysWritten = "trunkId";
    private static readonly string[] DialOmittedWhenNull = ["correlationId", "error"];
    private static readonly string[] TransferOmittedWhenNull = ["error"];

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo?.Type;
        if (type == typeof(VoiceDialResponse))
            Apply(schema, DialOmittedWhenNull);
        else if (type == typeof(VoiceTransferResponse))
            Apply(schema, TransferOmittedWhenNull);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes <paramref name="omittedWhenNull"/> from the schema's <c>required</c> set and adds <c>trunkId</c> when the
    /// schema has that property. Idempotent.
    /// </summary>
    internal static void Apply(OpenApiSchema schema, IEnumerable<string> omittedWhenNull)
    {
        schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in omittedWhenNull)
            schema.Required.Remove(name);
        if (schema.Properties?.ContainsKey(AlwaysWritten) == true)
            schema.Required.Add(AlwaysWritten);
    }
}
