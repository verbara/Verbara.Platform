using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Asterisk.Platform.Api.Health;

/// <summary>
/// Writes a <see cref="HealthReport"/> as JSON with a per-check circuit-state
/// breakdown for operator visibility on <c>/health/ready</c>.
/// </summary>
internal static class HealthReportJsonWriter
{
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("status", report.Status.ToString());
            writer.WriteNumber("totalDurationMs", report.TotalDuration.TotalMilliseconds);

            writer.WriteStartObject("checks");
            foreach (var (name, entry) in report.Entries)
            {
                writer.WriteStartObject(name);
                writer.WriteString("status", entry.Status.ToString());
                writer.WriteNumber("durationMs", entry.Duration.TotalMilliseconds);
                if (!string.IsNullOrEmpty(entry.Description))
                    writer.WriteString("description", entry.Description);
                if (entry.Exception is not null)
                    writer.WriteString("error", entry.Exception.Message);

                if (entry.Data.Count > 0)
                {
                    writer.WriteStartObject("data");
                    foreach (var (key, value) in entry.Data)
                    {
                        writer.WritePropertyName(key);
                        WriteValue(writer, value);
                    }
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt);
                break;
            case IReadOnlyDictionary<string, object> nested:
                writer.WriteStartObject();
                foreach (var (k, v) in nested)
                {
                    writer.WritePropertyName(k);
                    WriteValue(writer, v);
                }
                writer.WriteEndObject();
                break;
            default:
                // Fall back to anonymous / primitive coercion. Keep it robust by stringifying
                // so the writer never throws on an unexpected shape; the fallback is only
                // exercised for per-check data objects we own.
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
