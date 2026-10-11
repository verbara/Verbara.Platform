using Microsoft.OpenApi;
using Verbara.Platform.Api.OpenApi;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// Unit tests for <see cref="NullableCredentialValuesTransformer"/> (whatsapp-works-for-real D15): the values
/// of the <c>credentials</c> map become <c>string | null</c>, idempotently, and a schema without that map is
/// left untouched.
/// </summary>
public sealed class NullableCredentialValuesTransformerTests
{
    private static OpenApiSchema BuildRequestSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["isActive"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ["credentials"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object | JsonSchemaType.Null,
                AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        },
    };

    private static JsonSchemaType? ValueType(OpenApiSchema schema) =>
        ((OpenApiSchema)((OpenApiSchema)schema.Properties!["credentials"]).AdditionalProperties!).Type;

    [Fact]
    public void MakeCredentialValuesNullable_ShouldAddNull_WhenValuesAreString()
    {
        var schema = BuildRequestSchema();

        NullableCredentialValuesTransformer.MakeCredentialValuesNullable(schema);

        ValueType(schema).Should().Be(JsonSchemaType.String | JsonSchemaType.Null);
    }

    [Fact]
    public void MakeCredentialValuesNullable_ShouldBeIdempotent_WhenAppliedTwice()
    {
        var schema = BuildRequestSchema();

        NullableCredentialValuesTransformer.MakeCredentialValuesNullable(schema);
        NullableCredentialValuesTransformer.MakeCredentialValuesNullable(schema);

        ValueType(schema).Should().Be(JsonSchemaType.String | JsonSchemaType.Null);
        ((OpenApiSchema)schema.Properties!["isActive"]).Type.Should().Be(JsonSchemaType.Boolean);
    }

    [Fact]
    public void MakeCredentialValuesNullable_ShouldLeaveSchemaUntouched_WhenNoCredentialsProperty()
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema> { ["name"] = new OpenApiSchema { Type = JsonSchemaType.String } },
        };

        NullableCredentialValuesTransformer.MakeCredentialValuesNullable(schema);

        ((OpenApiSchema)schema.Properties["name"]).Type.Should().Be(JsonSchemaType.String);
    }
}
