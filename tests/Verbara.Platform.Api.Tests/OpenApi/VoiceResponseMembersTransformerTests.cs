using Microsoft.OpenApi;
using Verbara.Platform.Api.OpenApi;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// Unit tests for <see cref="VoiceResponseMembersTransformer"/> (outbound-calls-reach-the-trunk, design D4): the members
/// omitted when null leave <c>required</c>, the always-written <c>trunkId</c> joins it, idempotently.
/// </summary>
public sealed class VoiceResponseMembersTransformerTests
{
    private static OpenApiSchema DialSchema(bool withTrunkId = true)
    {
        var properties = new Dictionary<string, IOpenApiSchema>
        {
            ["accepted"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ["correlationId"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            ["error"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
        };
        if (withTrunkId)
            properties["trunkId"] = new OpenApiSchema { Type = JsonSchemaType.Integer | JsonSchemaType.Null };
        return new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = properties,
            Required = new HashSet<string> { "accepted", "correlationId", "error" },
        };
    }

    [Fact]
    public void Apply_ShouldRequireTrunkId_AndReleaseTheMembersOmittedWhenNull()
    {
        var schema = DialSchema();

        VoiceResponseMembersTransformer.Apply(schema, ["correlationId", "error"]);

        schema.Required.Should().BeEquivalentTo(["accepted", "trunkId"]);
    }

    [Fact]
    public void Apply_ShouldBeIdempotent_WhenAppliedTwice()
    {
        var schema = DialSchema();

        VoiceResponseMembersTransformer.Apply(schema, ["correlationId", "error"]);
        VoiceResponseMembersTransformer.Apply(schema, ["correlationId", "error"]);

        schema.Required.Should().BeEquivalentTo(["accepted", "trunkId"]);
    }

    [Fact]
    public void Apply_ShouldNotRequireTrunkId_WhenTheSchemaHasNoSuchProperty()
    {
        var schema = DialSchema(withTrunkId: false);

        VoiceResponseMembersTransformer.Apply(schema, ["error"]);

        schema.Required.Should().BeEquivalentTo(["accepted", "correlationId"]);
    }
}
