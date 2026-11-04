using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Verbara.Platform.Api.Endpoints;

namespace Verbara.Platform.Api.OpenApi;

/// <summary>
/// Makes the emitted OpenAPI schemas of the contact's conversation history and of a conversation's history say which
/// members are always written (contact-history-opens-by-assignment, design D14). <see cref="Serialization.ApiJsonContext"/>
/// omits a null member (<c>WhenWritingNull</c>), while the .NET 10 schema exporter lists every constructor parameter
/// without a default as required, nullable or not. This removes the omitted-when-null members of
/// <see cref="ContactConversationSummaryDto"/>, <see cref="ConversationDispositionDto"/> and
/// <see cref="MessageHistoryPageDto"/> from <c>required</c>; <see cref="ConversationLastMessageDto"/> omits nothing.
/// </summary>
/// <remarks>
/// The per-DTO posture of <see cref="VoiceResponseMembersTransformer"/>; <c>openapi-required-follows-omitted-nulls</c>
/// may later replace both with a document-wide rule. Document-only and AOT-safe: the targets are identified by a
/// compile-time <c>typeof</c> match, no reflection over user types.
/// </remarks>
internal sealed class ContactHistoryResponseMembersTransformer : IOpenApiSchemaTransformer
{
    private static readonly string[] SummaryOmittedWhenNull =
    [
        "closedAt", "durationSeconds", "closedReason", "queueId", "queueName", "agentId", "agentDisplayName",
        "disposition", "lastMessage",
    ];

    private static readonly string[] DispositionOmittedWhenNull = ["label"];
    private static readonly string[] HistoryPageOmittedWhenNull = ["nextBefore"];

    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo?.Type;
        if (type == typeof(ContactConversationSummaryDto))
            Release(schema, SummaryOmittedWhenNull);
        else if (type == typeof(ConversationDispositionDto))
            Release(schema, DispositionOmittedWhenNull);
        else if (type == typeof(MessageHistoryPageDto))
            Release(schema, HistoryPageOmittedWhenNull);

        return Task.CompletedTask;
    }

    /// <summary>Removes <paramref name="omittedWhenNull"/> from the schema's <c>required</c> set. Idempotent.</summary>
    internal static void Release(OpenApiSchema schema, IEnumerable<string> omittedWhenNull)
    {
        if (schema.Required is null)
            return;
        foreach (var name in omittedWhenNull)
            schema.Required.Remove(name);
    }
}
