using Microsoft.OpenApi;
using Verbara.Platform.Api.OpenApi;

namespace Verbara.Platform.Api.Tests.OpenApi;

/// <summary>
/// Unit tests for <see cref="ContactHistoryResponseMembersTransformer"/> (contact-history-opens-by-assignment, design
/// D14): the members omitted when null leave <c>required</c>, idempotently, and nothing else changes.
/// </summary>
public sealed class ContactHistoryResponseMembersTransformerTests
{
    private static OpenApiSchema PageSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["items"] = new OpenApiSchema { Type = JsonSchemaType.Array },
            ["hasMore"] = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ["nextBefore"] = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
        },
        Required = new HashSet<string> { "items", "hasMore", "nextBefore" },
    };

    [Fact]
    public void Release_ShouldDropOnlyTheMembersOmittedWhenNull_WhenTheSchemaRequiresThem()
    {
        var schema = PageSchema();

        ContactHistoryResponseMembersTransformer.Release(schema, ["nextBefore"]);

        schema.Required.Should().BeEquivalentTo(["items", "hasMore"]);
        schema.Properties.Should().ContainKey("nextBefore", "the member stays documented, only optional");
    }

    [Fact]
    public void Release_ShouldBeIdempotent_WhenAppliedTwice()
    {
        var schema = PageSchema();

        ContactHistoryResponseMembersTransformer.Release(schema, ["nextBefore"]);
        ContactHistoryResponseMembersTransformer.Release(schema, ["nextBefore"]);

        schema.Required.Should().BeEquivalentTo(["items", "hasMore"]);
    }

    [Fact]
    public void Release_ShouldLeaveTheSchemaWithoutARequiredSet_WhenItHasNone()
    {
        var schema = PageSchema();
        schema.Required = null;

        ContactHistoryResponseMembersTransformer.Release(schema, ["nextBefore"]);

        schema.Required.Should().BeNull();
    }
}
