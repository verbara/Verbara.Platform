using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Verbara.Platform.Api.Endpoints;

namespace Verbara.Platform.Api.OpenApi;

/// <summary>
/// Declares the values of <see cref="UpdateChannelConfigRequest"/>'s <c>credentials</c> map as
/// <c>string | null</c> in the emitted OpenAPI document (whatsapp-works-for-real D15): a <see langword="null"/>
/// value removes the stored key, so the generated Web client must be allowed to send one. The .NET 10 schema
/// exporter drops the nullability of a dictionary's value type argument and emits plain <c>string</c>.
/// </summary>
/// <remarks>
/// Document-only, the posture of <see cref="ComplianceSeverityEnumTransformer"/> (Platform/ADR-0036): the DTO
/// is already <c>Dictionary&lt;string, string?&gt;</c> and its source-generated (de)serializer already accepts
/// null. AOT-safe: the target is identified by a compile-time <c>typeof</c> match, no reflection over user types.
/// </remarks>
internal sealed class NullableCredentialValuesTransformer : IOpenApiSchemaTransformer
{
    /// <summary>The camelCase property name of <c>UpdateChannelConfigRequest.Credentials</c>.</summary>
    private const string CredentialsPropertyName = "credentials";

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo?.Type == typeof(UpdateChannelConfigRequest))
            MakeCredentialValuesNullable(schema);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds <c>null</c> to the type of the <c>credentials</c> map's values. Idempotent; a schema without that
    /// property, or whose values are not a concrete schema, is left untouched.
    /// </summary>
    internal static void MakeCredentialValuesNullable(OpenApiSchema schema)
    {
        if (schema.Properties is not { } properties
            || !properties.TryGetValue(CredentialsPropertyName, out IOpenApiSchema? credentials)
            || credentials is not OpenApiSchema { AdditionalProperties: OpenApiSchema values })
        {
            return;
        }

        values.Type = (values.Type ?? JsonSchemaType.String) | JsonSchemaType.Null;
    }
}
