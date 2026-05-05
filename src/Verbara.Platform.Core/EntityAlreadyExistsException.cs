namespace Verbara.Platform.Core;

/// <summary>
/// Thrown by a storage-layer <c>SaveAsync</c> when the insert collides with a
/// pre-existing entity that the storage layer cannot UPSERT. Endpoint
/// handlers catch this and translate to HTTP 409 Conflict.
/// </summary>
/// <remarks>
/// Pre-v1.14.3, the Postgres stores let <c>NpgsqlException</c> with
/// SqlState <c>23505</c> bubble out unchanged → ASP.NET's default
/// problem-handler returned HTTP 500 with the raw Postgres constraint name
/// in the response body (R5.5 P0 finding #4). This domain exception
/// standardizes the contract: the storage layer translates the
/// vendor-specific 23505 into <see cref="EntityAlreadyExistsException"/>,
/// the endpoint translates the domain exception into HTTP 409 with a
/// stable problem-details body that callers can parse.
/// </remarks>
public sealed class EntityAlreadyExistsException : Exception
{
    /// <summary>
    /// The entity kind that collided (e.g. <c>"user"</c>, <c>"queue"</c>).
    /// Used by the endpoint to populate the problem-details <c>title</c>.
    /// </summary>
    public string EntityKind { get; }

    /// <summary>
    /// The conflicting field (e.g. <c>"email"</c>, <c>"name"</c>) — for
    /// logging + problem-details detail. Storage callers may pass
    /// <c>null</c> when the field cannot be cheaply identified from the
    /// SqlState alone.
    /// </summary>
    public string? ConflictingField { get; }

    public EntityAlreadyExistsException(string entityKind, string? conflictingField, Exception? innerException = null)
        : base(BuildMessage(entityKind, conflictingField), innerException)
    {
        ArgumentNullException.ThrowIfNull(entityKind);
        EntityKind = entityKind;
        ConflictingField = conflictingField;
    }

    private static string BuildMessage(string entityKind, string? conflictingField) =>
        conflictingField is null
            ? $"A {entityKind} with the supplied identifier already exists."
            : $"A {entityKind} with the supplied {conflictingField} already exists.";
}
