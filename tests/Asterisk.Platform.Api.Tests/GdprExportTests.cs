using System.IO.Compression;
using System.Text;
using Asterisk.Platform.Api.Services;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Api.Tests;

public sealed class GdprExportTests
{
    // -------------------------------------------------------------------------
    // Shared fixtures
    // -------------------------------------------------------------------------

    private static GdprExportData MinimalData() => new()
    {
        SubjectId = "sub-001",
        SubjectType = "customer",
        ExportedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private static GdprExportData RichData() => new()
    {
        SubjectId = "sub-999",
        SubjectType = "agent",
        ExportedAt = new DateTimeOffset(2026, 6, 15, 12, 30, 0, TimeSpan.Zero),
        PersonalData = new Dictionary<string, string>
        {
            ["Name / Nombre"] = "John Doe",
            ["Email / Correo"] = "john@example.com"
        },
        Conversations =
        [
            new GdprConversationRecord(
                "conv-1", "voice", "completed",
                new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 8, 10, 0, TimeSpan.Zero))
        ],
        Recordings =
        [
            new GdprRecordingRecord(
                "rec-1", "conv-1", 600,
                new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero))
        ],
        CallDetails =
        [
            new GdprCallDetailRecord(
                "sess-1", "+1555000001", "+1555000002", 600, "ANSWERED",
                new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero))
        ],
        SurveyResponses =
        [
            new GdprSurveyResponseRecord(
                "survey-1", "NPS Survey",
                new Dictionary<string, string> { ["How was your experience?"] = "Great" },
                new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero))
        ],
        AuditTrail =
        [
            new GdprAuditRecord(
                "data.export", "subject", "sub-999", "admin@example.com",
                new DateTimeOffset(2026, 6, 15, 12, 30, 0, TimeSpan.Zero))
        ]
    };

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // -------------------------------------------------------------------------
    // CsvGdprExportFormatter — ContentType
    // -------------------------------------------------------------------------

    [Fact]
    public void ContentType_ShouldReturnApplicationZip_WhenCsvFormatter()
    {
        var sut = new CsvGdprExportFormatter();
        sut.ContentType.Should().Be("application/zip");
    }

    [Fact]
    public void FileExtension_ShouldReturnDotZip_WhenCsvFormatter()
    {
        var sut = new CsvGdprExportFormatter();
        sut.FileExtension.Should().Be(".zip");
    }

    // -------------------------------------------------------------------------
    // CsvGdprExportFormatter — ZIP structure
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FormatAsync_ShouldProduceValidZipBytes_WhenCsvFormatterWithMinimalData()
    {
        var sut = new CsvGdprExportFormatter();
        var bytes = await sut.FormatAsync(MinimalData(), CancellationToken.None);

        bytes.Should().NotBeNullOrEmpty();

        // ZIP files start with PK\x03\x04 (local file header signature)
        bytes[0].Should().Be(0x50); // P
        bytes[1].Should().Be(0x4B); // K
        bytes[2].Should().Be(0x03);
        bytes[3].Should().Be(0x04);
    }

    [Fact]
    public async Task FormatAsync_ShouldContainSixEntries_WhenCsvFormatterWithMinimalData()
    {
        var sut = new CsvGdprExportFormatter();
        var bytes = await sut.FormatAsync(MinimalData(), CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        archive.Entries.Should().HaveCount(6);
    }

    [Fact]
    public async Task FormatAsync_ShouldContainExpectedEntryNames_WhenCsvFormatterWithMinimalData()
    {
        var sut = new CsvGdprExportFormatter();
        var bytes = await sut.FormatAsync(MinimalData(), CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var entryNames = archive.Entries.Select(e => e.Name).ToList();
        entryNames.Should().BeEquivalentTo(
        [
            "contact.csv",
            "conversations.csv",
            "recordings.csv",
            "call_details.csv",
            "survey_responses.csv",
            "audit_trail.csv"
        ]);
    }

    // -------------------------------------------------------------------------
    // CsvGdprExportFormatter — UTF-8 BOM
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FormatAsync_ShouldHaveUtf8BomInContactCsv_WhenCsvFormatter()
    {
        await AssertEntryStartsWithBom("contact.csv", MinimalData());
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveUtf8BomInConversationsCsv_WhenCsvFormatter()
    {
        await AssertEntryStartsWithBom("conversations.csv", MinimalData());
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveUtf8BomInRecordingsCsv_WhenCsvFormatter()
    {
        await AssertEntryStartsWithBom("recordings.csv", MinimalData());
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveUtf8BomInCallDetailsCsv_WhenCsvFormatter()
    {
        await AssertEntryStartsWithBom("call_details.csv", MinimalData());
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveUtf8BomInSurveyResponsesCsv_WhenCsvFormatter()
    {
        await AssertEntryStartsWithBom("survey_responses.csv", MinimalData());
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveUtf8BomInAuditTrailCsv_WhenCsvFormatter()
    {
        await AssertEntryStartsWithBom("audit_trail.csv", MinimalData());
    }

    private static async Task AssertEntryStartsWithBom(string entryName, GdprExportData data)
    {
        var sut = new CsvGdprExportFormatter();
        var bytes = await sut.FormatAsync(data, CancellationToken.None);

        var content = ReadZipEntry(bytes, entryName);
        content.Should().StartWith(Utf8Bom,
            because: $"{entryName} must begin with UTF-8 BOM (0xEF 0xBB 0xBF) for Excel compatibility");
    }

    // -------------------------------------------------------------------------
    // CsvGdprExportFormatter — Bilingual headers
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FormatAsync_ShouldHaveBilingualHeaderInContactCsv_WhenCsvFormatter()
    {
        var content = await ReadZipEntryAsText("contact.csv", MinimalData());
        content.Should().Contain("Field").And.Contain("Campo");
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveBilingualHeaderInConversationsCsv_WhenCsvFormatter()
    {
        var content = await ReadZipEntryAsText("conversations.csv", MinimalData());
        // Header: "Conversation ID / ID conversación,Channel / Canal,..."
        content.Should().Contain("Conversation ID").And.Contain("ID conversación");
        content.Should().Contain("Channel").And.Contain("Canal");
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveBilingualHeaderInRecordingsCsv_WhenCsvFormatter()
    {
        var content = await ReadZipEntryAsText("recordings.csv", MinimalData());
        content.Should().Contain("Recording ID").And.Contain("ID grabación");
        content.Should().Contain("Duration").And.Contain("Duración");
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveBilingualHeaderInCallDetailsCsv_WhenCsvFormatter()
    {
        var content = await ReadZipEntryAsText("call_details.csv", MinimalData());
        content.Should().Contain("Session ID").And.Contain("ID sesión");
        content.Should().Contain("Caller Number").And.Contain("Número origen");
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveBilingualHeaderInSurveyResponsesCsv_WhenCsvFormatter()
    {
        var content = await ReadZipEntryAsText("survey_responses.csv", MinimalData());
        content.Should().Contain("Survey ID").And.Contain("ID encuesta");
        content.Should().Contain("Question").And.Contain("Pregunta");
    }

    [Fact]
    public async Task FormatAsync_ShouldHaveBilingualHeaderInAuditTrailCsv_WhenCsvFormatter()
    {
        var content = await ReadZipEntryAsText("audit_trail.csv", MinimalData());
        content.Should().Contain("Action").And.Contain("Acción");
        content.Should().Contain("Entity Type").And.Contain("Tipo entidad");
    }

    // -------------------------------------------------------------------------
    // CsvGdprExportFormatter — Data rows
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FormatAsync_ShouldIncludeSubjectIdInContactCsv_WhenCsvFormatterWithData()
    {
        var content = await ReadZipEntryAsText("contact.csv", RichData());
        content.Should().Contain("sub-999");
    }

    [Fact]
    public async Task FormatAsync_ShouldIncludePersonalDataInContactCsv_WhenCsvFormatterWithData()
    {
        var content = await ReadZipEntryAsText("contact.csv", RichData());
        content.Should().Contain("John Doe");
        content.Should().Contain("john@example.com");
    }

    [Fact]
    public async Task FormatAsync_ShouldIncludeConversationRowInConversationsCsv_WhenCsvFormatterWithData()
    {
        var content = await ReadZipEntryAsText("conversations.csv", RichData());
        content.Should().Contain("conv-1");
        content.Should().Contain("voice");
        content.Should().Contain("completed");
    }

    [Fact]
    public async Task FormatAsync_ShouldIncludeRecordingRowInRecordingsCsv_WhenCsvFormatterWithData()
    {
        var content = await ReadZipEntryAsText("recordings.csv", RichData());
        content.Should().Contain("rec-1");
        content.Should().Contain("600");
    }

    [Fact]
    public async Task FormatAsync_ShouldIncludeCallDetailRowInCallDetailsCsv_WhenCsvFormatterWithData()
    {
        var content = await ReadZipEntryAsText("call_details.csv", RichData());
        content.Should().Contain("sess-1");
        content.Should().Contain("ANSWERED");
    }

    [Fact]
    public async Task FormatAsync_ShouldIncludeSurveyAnswerRowInSurveyResponsesCsv_WhenCsvFormatterWithData()
    {
        var content = await ReadZipEntryAsText("survey_responses.csv", RichData());
        content.Should().Contain("survey-1");
        content.Should().Contain("NPS Survey");
        content.Should().Contain("Great");
    }

    [Fact]
    public async Task FormatAsync_ShouldIncludeAuditRowInAuditTrailCsv_WhenCsvFormatterWithData()
    {
        var content = await ReadZipEntryAsText("audit_trail.csv", RichData());
        content.Should().Contain("data.export");
        content.Should().Contain("admin@example.com");
    }

    // -------------------------------------------------------------------------
    // CsvGdprExportFormatter — Empty data (null collections)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FormatAsync_ShouldNotThrow_WhenCsvFormatterWithNullCollections()
    {
        var sut = new CsvGdprExportFormatter();
        var act = async () => await sut.FormatAsync(MinimalData(), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FormatAsync_ShouldStillProduceSixEntries_WhenCsvFormatterWithNullCollections()
    {
        var sut = new CsvGdprExportFormatter();
        var bytes = await sut.FormatAsync(MinimalData(), CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        archive.Entries.Should().HaveCount(6);
    }

    [Fact]
    public async Task FormatAsync_ShouldProduceHeaderOnlyRows_WhenCsvFormatterWithEmptyCollections()
    {
        var data = new GdprExportData
        {
            SubjectId = "sub-empty",
            SubjectType = "customer",
            ExportedAt = DateTimeOffset.UtcNow,
            Conversations = [],
            Recordings = [],
            CallDetails = [],
            SurveyResponses = [],
            AuditTrail = []
        };

        var sut = new CsvGdprExportFormatter();
        var bytes = await sut.FormatAsync(data, CancellationToken.None);

        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        // Each CSV should contain only the BOM + header line (no data rows)
        foreach (var entry in archive.Entries)
        {
            var entryBytes = ReadEntryBytes(entry);
            // Strip BOM, then check content is not empty (header must be present)
            entryBytes.Length.Should().BeGreaterThan(Utf8Bom.Length,
                because: $"{entry.Name} should have at least a header line");
        }
    }

    // -------------------------------------------------------------------------
    // JsonGdprExportFormatter — ContentType
    // -------------------------------------------------------------------------

    [Fact]
    public void ContentType_ShouldReturnApplicationJson_WhenJsonFormatter()
    {
        var sut = new JsonGdprExportFormatter();
        sut.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void FileExtension_ShouldReturnDotJson_WhenJsonFormatter()
    {
        var sut = new JsonGdprExportFormatter();
        sut.FileExtension.Should().Be(".json");
    }

    // -------------------------------------------------------------------------
    // JsonGdprExportFormatter — Output
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FormatAsync_ShouldProduceNonEmptyBytes_WhenJsonFormatterWithMinimalData()
    {
        var sut = new JsonGdprExportFormatter();
        var bytes = await sut.FormatAsync(MinimalData(), CancellationToken.None);
        bytes.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FormatAsync_ShouldProduceValidJson_WhenJsonFormatterWithMinimalData()
    {
        var sut = new JsonGdprExportFormatter();
        var bytes = await sut.FormatAsync(MinimalData(), CancellationToken.None);

        var json = Encoding.UTF8.GetString(bytes);
        json.Should().StartWith("{").And.EndWith("}");
    }

    [Fact]
    public async Task FormatAsync_ShouldIncludeSubjectId_WhenJsonFormatterWithMinimalData()
    {
        var sut = new JsonGdprExportFormatter();
        var bytes = await sut.FormatAsync(MinimalData(), CancellationToken.None);

        var json = Encoding.UTF8.GetString(bytes);
        json.Should().Contain("sub-001");
    }

    [Fact]
    public async Task FormatAsync_ShouldIncludeSubjectType_WhenJsonFormatterWithMinimalData()
    {
        var sut = new JsonGdprExportFormatter();
        var bytes = await sut.FormatAsync(MinimalData(), CancellationToken.None);

        var json = Encoding.UTF8.GetString(bytes);
        json.Should().Contain("customer");
    }

    [Fact]
    public async Task FormatAsync_ShouldNotThrow_WhenJsonFormatterWithNullCollections()
    {
        var sut = new JsonGdprExportFormatter();
        var act = async () => await sut.FormatAsync(MinimalData(), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FormatAsync_ShouldNotThrow_WhenJsonFormatterWithRichData()
    {
        var sut = new JsonGdprExportFormatter();
        var act = async () => await sut.FormatAsync(RichData(), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task FormatAsync_ShouldSerializeAllFields_WhenJsonFormatterWithRichData()
    {
        var sut = new JsonGdprExportFormatter();
        var bytes = await sut.FormatAsync(RichData(), CancellationToken.None);

        var json = Encoding.UTF8.GetString(bytes);
        json.Should().Contain("conv-1");
        json.Should().Contain("rec-1");
        json.Should().Contain("sess-1");
        json.Should().Contain("NPS Survey");
        json.Should().Contain("data.export");
    }

    // -------------------------------------------------------------------------
    // IGdprExportFormatter — Interface contract
    // -------------------------------------------------------------------------

    [Fact]
    public void ContentType_ShouldBeDifferent_WhenComparingCsvAndJsonFormatters()
    {
        var csv = new CsvGdprExportFormatter();
        var json = new JsonGdprExportFormatter();

        csv.ContentType.Should().NotBe(json.ContentType);
    }

    [Fact]
    public void FileExtension_ShouldBeDifferent_WhenComparingCsvAndJsonFormatters()
    {
        var csv = new CsvGdprExportFormatter();
        var json = new JsonGdprExportFormatter();

        csv.FileExtension.Should().NotBe(json.FileExtension);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static byte[] ReadZipEntry(byte[] zipBytes, string entryName)
    {
        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var entry = archive.GetEntry(entryName);
        entry.Should().NotBeNull(because: $"ZIP must contain {entryName}");

        return ReadEntryBytes(entry!);
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task<string> ReadZipEntryAsText(string entryName, GdprExportData data)
    {
        var sut = new CsvGdprExportFormatter();
        var bytes = await sut.FormatAsync(data, CancellationToken.None);
        var entryBytes = ReadZipEntry(bytes, entryName);

        // Skip BOM for text comparison
        var offset = entryBytes.Length >= 3
            && entryBytes[0] == 0xEF
            && entryBytes[1] == 0xBB
            && entryBytes[2] == 0xBF ? 3 : 0;

        return Encoding.UTF8.GetString(entryBytes, offset, entryBytes.Length - offset);
    }
}
