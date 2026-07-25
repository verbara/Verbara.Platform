using Microsoft.OpenApi;
using Verbara.Platform.Api.OpenApi;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// Unit tests for <see cref="NumericSchemaTruthTransformer"/> (openapi-numeric-schema-truth,
/// Platform/ADR-0036). Exercises the pure rewrite over the Microsoft.OpenApi 2.9.0
/// <see cref="OpenApiSchema.Type"/> <c>[Flags]</c> enum: the spurious .NET 10
/// <c>["integer"|"number","string"]</c> union collapses to its single numeric type while
/// <see cref="OpenApiSchema.Format"/>, the <see cref="JsonSchemaType.Null"/> (nullability) flag,
/// and pure schemas are all preserved (dotnet/aspnetcore #64145).
/// </summary>
public sealed class NumericSchemaTruthTransformerTests
{
    [Fact]
    public void RewriteNumericStringUnion_ShouldCollapseToInteger_WhenIntegerStringUnion()
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer | JsonSchemaType.String,
            Format = "int32",
        };

        NumericSchemaTruthTransformer.RewriteNumericStringUnion(schema);

        schema.Type.Should().Be(JsonSchemaType.Integer,
            "the spurious string arm must be stripped, leaving a single integer type");
        schema.Format.Should().Be("int32", "format must be preserved");
    }

    [Fact]
    public void RewriteNumericStringUnion_ShouldCollapseToNumber_WhenNumberStringUnion()
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Number | JsonSchemaType.String,
            Format = "double",
        };

        NumericSchemaTruthTransformer.RewriteNumericStringUnion(schema);

        schema.Type.Should().Be(JsonSchemaType.Number,
            "the spurious string arm must be stripped, leaving a single number type");
        schema.Format.Should().Be("double", "format must be preserved");
    }

    [Fact]
    public void RewriteNumericStringUnion_ShouldPreserveNullFlag_WhenNullableNumericStringUnion()
    {
        // A nullable numeric renders as `integer | string | null` in OpenAPI 3.1;
        // only the `string` arm is the artifact — nullability (Null flag) must survive.
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer | JsonSchemaType.String | JsonSchemaType.Null,
            Format = "int32",
        };

        NumericSchemaTruthTransformer.RewriteNumericStringUnion(schema);

        schema.Type.Should().Be(JsonSchemaType.Integer | JsonSchemaType.Null,
            "string is stripped but the Null (nullability) flag must be preserved");
        schema.Format.Should().Be("int32", "format must be preserved");
    }

    [Fact]
    public void RewriteNumericStringUnion_ShouldCollapseToInt64_WhenInt64StringUnion()
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer | JsonSchemaType.String,
            Format = "int64",
        };

        NumericSchemaTruthTransformer.RewriteNumericStringUnion(schema);

        schema.Type.Should().Be(JsonSchemaType.Integer, "blanket over int64 too");
        schema.Format.Should().Be("int64", "int64 format must be preserved (no exemption)");
    }

    [Fact]
    public void RewriteNumericStringUnion_ShouldLeaveUnchanged_WhenPureString()
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "date-time",
        };

        NumericSchemaTruthTransformer.RewriteNumericStringUnion(schema);

        schema.Type.Should().Be(JsonSchemaType.String,
            "a genuine string schema must be untouched — it is not the numeric artifact");
        schema.Format.Should().Be("date-time", "format must be preserved");
    }

    [Fact]
    public void RewriteNumericStringUnion_ShouldLeaveUnchanged_WhenPureInteger()
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer,
            Format = "int32",
        };

        NumericSchemaTruthTransformer.RewriteNumericStringUnion(schema);

        schema.Type.Should().Be(JsonSchemaType.Integer,
            "an already single-typed numeric must be untouched");
        schema.Format.Should().Be("int32", "format must be preserved");
    }

    [Fact]
    public void RewriteNumericStringUnion_ShouldLeaveUnchanged_WhenTypeIsNull()
    {
        var schema = new OpenApiSchema { Type = null };

        NumericSchemaTruthTransformer.RewriteNumericStringUnion(schema);

        schema.Type.Should().BeNull("a schema with no declared type must be untouched");
    }

    [Fact]
    public void RewriteNumericStringUnion_ShouldLeaveUnchanged_WhenObjectSchema()
    {
        var schema = new OpenApiSchema { Type = JsonSchemaType.Object };

        NumericSchemaTruthTransformer.RewriteNumericStringUnion(schema);

        schema.Type.Should().Be(JsonSchemaType.Object,
            "a non-numeric composite must be untouched");
    }
}
