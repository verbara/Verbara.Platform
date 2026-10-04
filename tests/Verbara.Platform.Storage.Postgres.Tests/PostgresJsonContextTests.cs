using Verbara.Platform.Conversations;

namespace Verbara.Platform.Storage.Postgres.Tests;

/// <summary>
/// <see cref="PostgresJson.Ctx"/> is the shared context for JSONB columns, and JSONB re-orders
/// object keys. <see cref="MessageEnvelope"/> is registered on it and carries the polymorphic
/// <see cref="MessageBlock"/>, whose <c>$type</c> discriminator is not first once read back.
/// </summary>
public class PostgresJsonContextTests
{
    [Fact]
    public void Deserialize_ShouldReadPolymorphicBlocks_WhenTypeDiscriminatorIsNotFirst()
    {
        const string jsonbText = """
            {"blocks": [{"text": "hello", "type": 0, "$type": "text"},
                        {"url": "https://x/i.jpg", "type": 1, "$type": "image", "mimeType": "image/jpeg"}]}
            """;

        var envelope = PostgresJson.Deserialize(jsonbText, PostgresJson.Ctx.MessageEnvelope);

        envelope.Should().NotBeNull();
        envelope!.Blocks.Select(b => b.GetType()).Should().Equal(typeof(TextBlock), typeof(ImageBlock));
    }

    [Fact]
    public void Ctx_ShouldAllowOutOfOrderMetadata_WhenConstructedWithExplicitOptions()
    {
        // The explicit options passed to the PostgresJsonContext constructor replace the
        // [JsonSourceGenerationOptions] attribute wholesale, so both must carry the setting.
        PostgresJson.Ctx.Options.AllowOutOfOrderMetadataProperties.Should().BeTrue();
    }
}
