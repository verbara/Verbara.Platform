using Verbara.Platform.Typification;

namespace Verbara.Platform.Storage.Postgres.Stores;

/// <summary>
/// JSONB wrapper for the four definition collections stored in the
/// <c>typification_schemas.definition</c> column. The scalar schema columns
/// (name, version, is_published, max_depth, created_at, updated_at) stay as
/// dedicated columns; everything else round-trips through this record via the
/// AOT source-gen context <see cref="PostgresJson.Ctx"/>.
/// </summary>
public sealed record TypificationSchemaDefinition(
    IReadOnlyList<TypificationNode> Nodes,
    IReadOnlyList<TypificationField> Fields,
    IReadOnlyList<DataDipDef> DataDips,
    TypificationAiConfig AiConfig);
