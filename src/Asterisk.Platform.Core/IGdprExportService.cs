namespace Asterisk.Platform.Core;

/// <summary>
/// GDPR Article 20 — Right to Data Portability. Exports all PII for a contact.
/// </summary>
public interface IGdprExportService
{
    Task<GdprExportResult> ExportContactDataAsync(
        string tenantId, string contactId, CancellationToken ct);
}

/// <summary>
/// Result of a GDPR data export. Contains all PII associated with the subject.
/// </summary>
public sealed class GdprExportResult
{
    public required string ExportId { get; init; }
    public required DateTimeOffset ExportedAt { get; init; }
    public required GdprSubjectInfo Subject { get; init; }
    public object? Contact { get; init; }
    public IReadOnlyList<object>? Conversations { get; init; }
    public IReadOnlyList<object>? Messages { get; init; }
    public IReadOnlyList<object>? AuthEvents { get; init; }
    public IReadOnlyList<object>? AuditEntries { get; init; }
}

public sealed record GdprSubjectInfo(string ContactId, string TenantId);
