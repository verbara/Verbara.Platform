using System.Reflection;
using Verbara.Platform.Channels.Core;

namespace Verbara.Platform.Channels.WhatsApp.Tests;

/// <summary>
/// The declared WhatsApp credential schema lives in Channels.Core (the storage layer wraps its secret keys
/// without referencing this module); this pins its Meta key names to <see cref="WhatsAppCredentialKeys"/>.
/// </summary>
public class WhatsAppCredentialSchemaTests
{
    [Fact]
    public void Schema_ShouldDeclareExactlyTheWhatsAppCredentialKeys_WhenComparedToConstants()
    {
        var constants = typeof(WhatsAppCredentialKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

        // defaultQueueId is the routing key every channel shares, not a Meta credential.
        ChannelCredentialSchemas.WhatsApp.Keys.Select(k => k.Name)
            .Where(name => name != ChannelCredentialSchemas.DefaultQueueIdKey)
            .Should().BeEquivalentTo(constants);
    }
}
