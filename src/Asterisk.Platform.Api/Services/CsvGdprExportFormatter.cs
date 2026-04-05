using System.IO.Compression;
using System.Text;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Produces a ZIP archive containing one CSV file per data category.
/// Each CSV uses UTF-8 BOM (Excel compatible) and bilingual headers (EN / ES).
/// </summary>
internal sealed class CsvGdprExportFormatter : IGdprExportFormatter
{
    public string ContentType => "application/zip";
    public string FileExtension => ".zip";

    // UTF-8 BOM
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public async ValueTask<byte[]> FormatAsync(GdprExportData data, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await AddEntryAsync(archive, "contact.csv", BuildContactCsv(data), ct);
            await AddEntryAsync(archive, "conversations.csv", BuildConversationsCsv(data), ct);
            await AddEntryAsync(archive, "recordings.csv", BuildRecordingsCsv(data), ct);
            await AddEntryAsync(archive, "call_details.csv", BuildCallDetailsCsv(data), ct);
            await AddEntryAsync(archive, "survey_responses.csv", BuildSurveyResponsesCsv(data), ct);
            await AddEntryAsync(archive, "audit_trail.csv", BuildAuditTrailCsv(data), ct);
        }

        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // CSV builders
    // -------------------------------------------------------------------------

    private static byte[] BuildContactCsv(GdprExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Field / Campo,Value / Valor");

        sb.AppendLine(CsvRow("Subject ID / ID del sujeto", data.SubjectId));
        sb.AppendLine(CsvRow("Subject Type / Tipo de sujeto", data.SubjectType));
        sb.AppendLine(CsvRow("Exported At / Exportado el", data.ExportedAt.ToString("O")));

        if (data.PersonalData is not null)
        {
            foreach (var (key, value) in data.PersonalData)
                sb.AppendLine(CsvRow(key, value));
        }

        return WithBom(sb.ToString());
    }

    private static byte[] BuildConversationsCsv(GdprExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Conversation ID / ID conversación,Channel / Canal,Status / Estado,Started At / Inicio,Ended At / Fin");

        foreach (var c in data.Conversations ?? [])
        {
            sb.AppendLine(string.Join(",",
                Escape(c.ConversationId),
                Escape(c.Channel),
                Escape(c.Status),
                Escape(c.StartedAt.ToString("O")),
                Escape(c.EndedAt?.ToString("O") ?? string.Empty)));
        }

        return WithBom(sb.ToString());
    }

    private static byte[] BuildRecordingsCsv(GdprExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Recording ID / ID grabación,Conversation ID / ID conversación,Duration (s) / Duración (s),Created At / Creado el");

        foreach (var r in data.Recordings ?? [])
        {
            sb.AppendLine(string.Join(",",
                Escape(r.RecordingId),
                Escape(r.ConversationId),
                r.DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Escape(r.CreatedAt.ToString("O"))));
        }

        return WithBom(sb.ToString());
    }

    private static byte[] BuildCallDetailsCsv(GdprExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Session ID / ID sesión,Caller Number / Número origen,Called Number / Número destino,Duration (s) / Duración (s),Disposition / Disposición,Started At / Inicio");

        foreach (var c in data.CallDetails ?? [])
        {
            sb.AppendLine(string.Join(",",
                Escape(c.SessionId),
                Escape(c.CallerNumber),
                Escape(c.CalledNumber),
                c.DurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Escape(c.Disposition),
                Escape(c.StartedAt.ToString("O"))));
        }

        return WithBom(sb.ToString());
    }

    private static byte[] BuildSurveyResponsesCsv(GdprExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Survey ID / ID encuesta,Survey Name / Nombre encuesta,Question / Pregunta,Answer / Respuesta,Submitted At / Enviado el");

        foreach (var s in data.SurveyResponses ?? [])
        {
            if (s.Answers.Count == 0)
            {
                sb.AppendLine(string.Join(",",
                    Escape(s.SurveyId),
                    Escape(s.SurveyName),
                    string.Empty,
                    string.Empty,
                    Escape(s.SubmittedAt.ToString("O"))));
            }
            else
            {
                foreach (var (question, answer) in s.Answers)
                {
                    sb.AppendLine(string.Join(",",
                        Escape(s.SurveyId),
                        Escape(s.SurveyName),
                        Escape(question),
                        Escape(answer),
                        Escape(s.SubmittedAt.ToString("O"))));
                }
            }
        }

        return WithBom(sb.ToString());
    }

    private static byte[] BuildAuditTrailCsv(GdprExportData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Action / Acción,Entity Type / Tipo entidad,Entity ID / ID entidad,Performed By / Ejecutado por,Occurred At / Ocurrido el");

        foreach (var a in data.AuditTrail ?? [])
        {
            sb.AppendLine(string.Join(",",
                Escape(a.Action),
                Escape(a.EntityType),
                Escape(a.EntityId),
                Escape(a.PerformedBy ?? string.Empty),
                Escape(a.OccurredAt.ToString("O"))));
        }

        return WithBom(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task AddEntryAsync(
        ZipArchive archive, string entryName, byte[] content, CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, ct);
    }

    /// <summary>Escapes a CSV field: wraps in quotes if it contains comma, quote, or newline.</summary>
    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    /// <summary>Builds a two-column CSV row with proper escaping.</summary>
    private static string CsvRow(string field, string value) =>
        $"{Escape(field)},{Escape(value)}";

    private static byte[] WithBom(string csv)
    {
        var content = Encoding.UTF8.GetBytes(csv);
        var result = new byte[Bom.Length + content.Length];
        Bom.CopyTo(result, 0);
        content.CopyTo(result, Bom.Length);
        return result;
    }
}
