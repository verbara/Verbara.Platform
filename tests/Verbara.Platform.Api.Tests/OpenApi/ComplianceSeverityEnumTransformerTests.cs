using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Verbara.Platform.Api.OpenApi;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// Unit tests for <see cref="ComplianceSeverityEnumTransformer"/> (openapi-residual-contract-shapes,
/// decision_ref Platform/ADR-0036). Exercises the pure rewrite over an
/// <see cref="OpenApiSchema"/>: the <c>ComplianceRuleSummaryDto.severity</c> string property is
/// narrowed to the closed enum <c>[Info, Warning, Critical]</c> while every sibling property and any
/// non-target schema stay untouched, and the rewrite is idempotent.
/// </summary>
public sealed class ComplianceSeverityEnumTransformerTests
{
    private static OpenApiSchema BuildComplianceRuleSummarySchema() => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["ruleId"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["ruleName"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["severity"] = new OpenApiSchema { Type = JsonSchemaType.String },
            ["occurrences"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
            ["sessionsAffected"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
            ["firstSeen"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" },
            ["lastSeen"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" },
        },
    };

    private static IReadOnlyList<string> EnumValues(IOpenApiSchema schema) =>
        ((OpenApiSchema)schema).Enum is { } nodes
            ? [.. nodes.Select(n => n!.GetValue<string>())]
            : [];

    [Fact]
    public void NarrowSeverityToEnum_ShouldNarrowSeverityToClosedEnum_WhenComplianceRuleSummarySchema()
    {
        var schema = BuildComplianceRuleSummarySchema();

        ComplianceSeverityEnumTransformer.NarrowSeverityToEnum(schema);

        var severity = schema.Properties!["severity"];
        ((OpenApiSchema)severity).Type.Should().Be(JsonSchemaType.String,
            "severity is a closed set of string literals, not a numeric or object type");
        // NB: the string-collection Equal overload takes params string[] (no `because`) — the
        // domain-ordering rationale is documented here rather than passed as an argument.
        EnumValues(severity).Should().Equal("Info", "Warning", "Critical");
    }

    [Fact]
    public void NarrowSeverityToEnum_ShouldLeaveSiblingProperties_WhenComplianceRuleSummarySchema()
    {
        var schema = BuildComplianceRuleSummarySchema();

        ComplianceSeverityEnumTransformer.NarrowSeverityToEnum(schema);

        // Only `severity` may gain an enum; every sibling property must be untouched.
        foreach (var name in new[] { "ruleId", "ruleName", "occurrences", "sessionsAffected", "firstSeen", "lastSeen" })
        {
            ((OpenApiSchema)schema.Properties![name]).Enum.Should().BeNullOrEmpty(
                $"'{name}' is not the severity property and must not gain an enum constraint");
        }

        ((OpenApiSchema)schema.Properties!["occurrences"]).Type.Should().Be(JsonSchemaType.Integer,
            "numeric sibling types must be preserved — the severity transformer is orthogonal to numeric schemas");
    }

    [Fact]
    public void NarrowSeverityToEnum_ShouldBeIdempotent_WhenAppliedTwice()
    {
        var schema = BuildComplianceRuleSummarySchema();

        ComplianceSeverityEnumTransformer.NarrowSeverityToEnum(schema);
        ComplianceSeverityEnumTransformer.NarrowSeverityToEnum(schema);

        // Idempotent: re-running the rewrite over an already-narrowed schema produces the same closed enum.
        EnumValues(schema.Properties!["severity"]).Should().Equal("Info", "Warning", "Critical");
    }

    [Fact]
    public void NarrowSeverityToEnum_ShouldNoOp_WhenSchemaHasNoSeverityProperty()
    {
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["ruleId"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        };

        ComplianceSeverityEnumTransformer.NarrowSeverityToEnum(schema);

        schema.Properties!.Should().ContainKey("ruleId");
        schema.Properties!.Should().NotContainKey("severity",
            "a schema without a severity property must be left entirely unchanged");
    }

    [Fact]
    public void NarrowSeverityToEnum_ShouldNoOp_WhenSchemaHasNoProperties()
    {
        var schema = new OpenApiSchema { Type = JsonSchemaType.String };

        // Must not throw when there is no Properties dictionary (e.g. a scalar schema).
        ComplianceSeverityEnumTransformer.NarrowSeverityToEnum(schema);

        schema.Type.Should().Be(JsonSchemaType.String, "a scalar schema is not the target and must be untouched");
    }
}
