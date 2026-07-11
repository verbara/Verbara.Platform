using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Testcontainers-backed round-trip suite for <see cref="PostgresCsatTemplateStore"/>
/// (csat-runner Phase E). Reuses <see cref="MigrationsFixture"/> so the store is exercised
/// against the REAL embedded migrations (incl. 016, which creates <c>csat_templates</c> with
/// the <c>chk_csat_templates_channel</c> CHECK constraint). Each test uses a unique tenant id
/// so no per-test truncation is needed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresCsatTemplateStoreTests : IClassFixture<MigrationsFixture>
{
    private readonly PostgresCsatTemplateStore _store;

    public PostgresCsatTemplateStoreTests(MigrationsFixture fixture)
        => _store = new PostgresCsatTemplateStore(fixture.DataSource);

    [Fact]
    public async Task SaveThenGetById_ShouldRoundTripAllColumns_WhenEmailTemplate()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var id = EntityId.From($"tmpl-{Guid.NewGuid():N}");
        var createdAt = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

        await _store.SaveAsync(new CsatTemplateEntry
        {
            TemplateId = id,
            TenantId = tenant,
            Channel = "email",
            Locale = "en-US",
            Subject = "How was your support?",
            Body = "Reply 1-5 to rate.",
            IsDefault = true,
            CreatedAt = createdAt,
        }, CancellationToken.None);

        var loaded = await _store.GetByIdAsync(tenant, id, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Channel.Should().Be("email");
        loaded.Locale.Should().Be("en-US");
        loaded.Subject.Should().Be("How was your support?");
        loaded.Body.Should().Be("Reply 1-5 to rate.");
        loaded.IsDefault.Should().BeTrue();
        loaded.CreatedAt.Should().Be(createdAt);
    }

    [Fact]
    public async Task SaveThenGetById_ShouldRoundTripNullSubject_WhenSmsTemplate()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var id = EntityId.From($"tmpl-{Guid.NewGuid():N}");

        await _store.SaveAsync(Entry(tenant, id, "sms", "es-419", subject: null, body: "Responde 1-5."), CancellationToken.None);

        var loaded = await _store.GetByIdAsync(tenant, id, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Subject.Should().BeNull();
        loaded.Body.Should().Be("Responde 1-5.");
    }

    [Fact]
    public async Task Save_ShouldUpsert_WhenTemplateIdExists()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var id = EntityId.From($"tmpl-{Guid.NewGuid():N}");

        await _store.SaveAsync(Entry(tenant, id, "email", "en-US", "Old", "Old body"), CancellationToken.None);
        await _store.SaveAsync(Entry(tenant, id, "email", "en-US", "New", "New body"), CancellationToken.None);

        var loaded = await _store.GetByIdAsync(tenant, id, CancellationToken.None);
        loaded!.Subject.Should().Be("New");
        loaded.Body.Should().Be("New body");
        loaded.UpdatedAt.Should().NotBeNull();

        var all = await _store.GetAllAsync(tenant, CancellationToken.None);
        all.Should().ContainSingle();
    }

    [Fact]
    public async Task GetByChannelAndLocaleAsync_ShouldPreferDefault_WhenBothPresent()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        await _store.SaveAsync(Entry(tenant, EntityId.From("a"), "email", "en-US", "Custom", "Custom body", isDefault: false), CancellationToken.None);
        await _store.SaveAsync(Entry(tenant, EntityId.From("b"), "email", "en-US", "Default", "Default body", isDefault: true), CancellationToken.None);

        var rows = await _store.GetByChannelAndLocaleAsync(tenant, "email", "en-US", CancellationToken.None);

        rows.Should().HaveCount(2);
        rows[0].IsDefault.Should().BeTrue(); // is_default DESC
    }

    [Fact]
    public async Task GetDefaultsByChannelAsync_ShouldReturnOnlyDefaults()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        await _store.SaveAsync(Entry(tenant, EntityId.From("d-enus"), "sms", "en-US", null, "en", isDefault: true), CancellationToken.None);
        await _store.SaveAsync(Entry(tenant, EntityId.From("d-es"), "sms", "es-419", null, "es", isDefault: true), CancellationToken.None);
        await _store.SaveAsync(Entry(tenant, EntityId.From("nondef"), "sms", "pt-BR", null, "pt", isDefault: false), CancellationToken.None);

        var rows = await _store.GetDefaultsByChannelAsync(tenant, "sms", CancellationToken.None);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.IsDefault);
    }

    [Fact]
    public async Task Delete_ShouldRemoveRow()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var id = EntityId.From($"tmpl-{Guid.NewGuid():N}");
        await _store.SaveAsync(Entry(tenant, id, "voice", "en-US", null, "Rate one to five."), CancellationToken.None);

        await _store.DeleteAsync(tenant, id, CancellationToken.None);

        (await _store.GetByIdAsync(tenant, id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Insert_ShouldBeRejected_WhenChannelInvalid()
    {
        var tenant = new TenantId($"t-{Guid.NewGuid():N}");
        var bad = Entry(tenant, EntityId.From("bad"), "webchat", "en-US", null, "x");

        var act = () => _store.SaveAsync(bad, CancellationToken.None);

        await act.Should().ThrowAsync<Npgsql.PostgresException>()
            .Where(e => e.SqlState == "23514"); // check_violation (chk_csat_templates_channel)
    }

    private static CsatTemplateEntry Entry(
        TenantId tenant, EntityId id, string channel, string locale, string? subject, string body, bool isDefault = false) => new()
    {
        TemplateId = id,
        TenantId = tenant,
        Channel = channel,
        Locale = locale,
        Subject = subject,
        Body = body,
        IsDefault = isDefault,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
