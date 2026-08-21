using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Pins the two halves of the <c>timestamptz</c> contract that the
/// <c>fix-local-kind-datetimeoffset</c> change established, against a real Postgres:
/// the read side (design D1 — reads yield <see cref="DateTimeKind.Utc"/>, so the ~55
/// <c>new DateTimeOffset(x, TimeSpan.Zero)</c> row projections are correct by construction)
/// and the write side (design D2 — the modern converter REJECTS any
/// <see cref="DateTimeOffset"/> whose <see cref="DateTimeOffset.Offset"/> is non-zero, so
/// every untrusted ingress must go through <c>ToUtcInstant()</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these tests are NOT vacuous on a UTC CI runner.</b> The obvious way to write a
/// "timezone" regression test — set a non-UTC <c>TZ</c> and assert nothing throws — proves
/// nothing where the runner is already UTC, because under the legacy Npgsql timestamp
/// switch <c>Local</c> and <c>Utc</c> share offset zero there and every construction
/// succeeds either way. These tests are therefore anchored on a property that differs
/// between the two converter selections <b>on every host, UTC included</b>: the
/// <see cref="DateTime.Kind"/> the reader hands back. Under the legacy switch a
/// <c>timestamptz</c> read yields <c>Kind == DateTimeKind.Local</c> even on a UTC machine
/// (offset zero, but the Kind is still <c>Local</c>); under the modern converter it yields
/// <c>Kind == DateTimeKind.Utc</c>. So the <c>Kind</c> assertions below go red the moment
/// anyone reinstates the switch, on any runner, in any timezone.
/// </para>
/// <para>
/// <b>Do not "simplify" the Kind assertions away.</b> They are the load-bearing tripwire;
/// the surrounding round-trip assertions only show the instant survives. Likewise, the
/// negative half of the write test (binding a raw non-zero offset MUST throw) is what
/// proves <c>ToUtcInstant()</c> is load-bearing rather than decorative — delete it and the
/// positive half passes even if the normalisation is removed.
/// </para>
/// </remarks>
[Collection("TimestampSemantics")]
public sealed class PostgresTimestampSemanticsTests
{
    private readonly TimestampSemanticsFixture _fixture;

    /// <summary>UTC-5, the reporter's host offset — no DST, so the shift is stable year-round.</summary>
    private const string NonUtcTimeZone = "America/Bogota";

    private static readonly TenantId Tenant = new("acme-timestamps");

    // Whole-second instants: Postgres timestamptz stores microseconds, so a value with
    // sub-microsecond ticks would be truncated and the UtcTicks comparisons would be noise.
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 20, 15, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 8, 20, 16, 45, 0, TimeSpan.Zero);

    public PostgresTimestampSemanticsTests(TimestampSemanticsFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Task 5.4 / spec scenario "A store projection survives a non-UTC process timezone".
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_ShouldProjectUtcKindWithoutThrowing_WhenProcessTimezoneIsNotUtc()
    {
        await _fixture.ResetAsync();

        var previousTz = Environment.GetEnvironmentVariable("TZ");
        try
        {
            // Reproduce the reporting host (UTC-5) for the duration of the test. This is
            // faithfulness to the scenario, NOT the source of the test's meaning: on a UTC CI
            // runner (or a box without tzdata, where this silently stays UTC) the assertions
            // below still discriminate legacy from modern via Kind. Safe to mutate here —
            // neither this test assembly nor Verbara.Platform.Storage.Postgres reads
            // TimeZoneInfo.Local / DateTime.Now anywhere.
            Environment.SetEnvironmentVariable("TZ", NonUtcTimeZone);
            TimeZoneInfo.ClearCachedData();

            var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
            await using (dataSource.ConfigureAwait(false))
            {
                var store = new PostgresCannedResponseStore(dataSource);
                var response = new CannedResponse
                {
                    ResponseId = EntityId.New(),
                    TenantId = Tenant,
                    Shortcut = "/greet",
                    Title = "Greeting",
                    Body = "Hello!",
                    CreatedBy = "seed-user",
                    CreatedAt = CreatedAt,
                    UpdatedAt = UpdatedAt,
                };

                await store.SaveAsync(response, CancellationToken.None);

                // --- THE TRIPWIRE -------------------------------------------------------
                // What the reader hands the row mappers. Utc under the modern converter,
                // Local under the legacy switch — on EVERY host, UTC runners included.
                var rawCreatedAt = await dataSource.QuerySingleAsync(
                    "SELECT created_at FROM canned_responses WHERE tenant_id = @TenantId AND response_id = @ResponseId",
                    p =>
                    {
                        p.Add(new NpgsqlParameter("TenantId", Tenant.Value));
                        p.Add(new NpgsqlParameter("ResponseId", response.ResponseId.Value));
                    },
                    static r => r.GetDateTime("created_at"),
                    CancellationToken.None);

                rawCreatedAt.Kind.Should().Be(
                    DateTimeKind.Utc,
                    "a timestamptz read must yield Kind=Utc; Kind=Local means the legacy Npgsql "
                    + "timestamp switch is back, which makes new DateTimeOffset(x, TimeSpan.Zero) "
                    + "throw on every non-UTC host");

                // --- The projection the ~55 row mappers actually perform ----------------
                var project = () => new DateTimeOffset(rawCreatedAt, TimeSpan.Zero);
                project.Should().NotThrow(
                    "the row projections construct DateTimeOffset at offset zero over the raw "
                    + "reader value, and that is legal only while the Kind is Utc");
                project().UtcTicks.Should().Be(CreatedAt.UtcTicks);

                // --- The real store projection, end to end ------------------------------
                var loaded = await store.GetByIdAsync(Tenant, response.ResponseId, CancellationToken.None);

                loaded.Should().NotBeNull();
                loaded!.CreatedAt.Offset.Should().Be(TimeSpan.Zero);
                loaded.CreatedAt.UtcTicks.Should().Be(CreatedAt.UtcTicks);
                loaded.UpdatedAt.Should().NotBeNull();
                loaded.UpdatedAt!.Value.Offset.Should().Be(TimeSpan.Zero);
                loaded.UpdatedAt.Value.UtcTicks.Should().Be(UpdatedAt.UtcTicks);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", previousTz);
            TimeZoneInfo.ClearCachedData();
        }
    }

    /// <summary>
    /// Task 5.5 / design D2. Both halves matter: the positive half shows the ingress
    /// normalisation round-trips the instant, the negative half shows the normalisation is
    /// the only reason the write is accepted at all.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ShouldNormaliseNonZeroOffset_WhenIngressValueGoesThroughToUtcInstant()
    {
        await _fixture.ResetAsync();

        // The ingress shape, verbatim from the endpoints: a client-supplied string carrying an
        // explicit -05:00 offset. ASP.NET Core's binder uses DateTimeStyles.AssumeUniversal,
        // which only supplies a *missing* offset — an explicit one survives into the parameter.
        var ingress = DateTimeOffset.Parse("2026-08-20T10:30:00-05:00", CultureInfo.InvariantCulture);
        ingress.Offset.Should().Be(TimeSpan.FromHours(-5), "the fixture value must actually carry a non-zero offset");

        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using (dataSource.ConfigureAwait(false))
        {
            var normalisedId = EntityId.New().Value;
            var rawId = EntityId.New().Value;

            // (a) Normalised at ingress -> accepted, and the stored instant is unchanged.
            await dataSource.ExecuteAsync(
                InsertSql,
                p => BindRow(p, normalisedId, ingress.ToUtcInstant()),
                CancellationToken.None);

            var stored = await dataSource.QuerySingleAsync(
                "SELECT created_at FROM canned_responses WHERE tenant_id = @TenantId AND response_id = @ResponseId",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", Tenant.Value));
                    p.Add(new NpgsqlParameter("ResponseId", normalisedId));
                },
                static r => r.GetDateTime("created_at"),
                CancellationToken.None);

            // Same tripwire as the read test — see the class remarks for why Kind, not TZ, is
            // what makes this non-vacuous on a UTC runner.
            stored.Kind.Should().Be(DateTimeKind.Utc);
            new DateTimeOffset(stored, TimeSpan.Zero).UtcTicks.Should().Be(
                ingress.UtcTicks,
                "ToUtcInstant() must convert (ToUniversalTime), not relabel (SpecifyKind) — a "
                + "relabel would shift the stored instant by five hours");

            // (b) The SAME value bound raw, with the SAME explicit NpgsqlDbType.TimestampTz ->
            // rejected. This is what makes (a) load-bearing rather than decorative: without the
            // ingress sweep every one of these binds — including the ~124 inside the compiled
            // Verbara.Sdk.Pro Postgres stores, which bind a DateTimeOffset directly and which
            // this repo cannot edit — would fail on any host whose local offset is not zero.
            var bindRaw = async () => await dataSource.ExecuteAsync(
                InsertSql,
                p => BindRow(p, rawId, ingress),
                CancellationToken.None);

            var thrown = (await bindRaw.Should().ThrowAsync<ArgumentException>(
                "the modern Npgsql converter rejects any non-zero Offset on a timestamptz write; "
                + "if this ever stops throwing, design D2's whole rationale for the ingress sweep "
                + "is void and the change must be revisited"))
                .Which;

            // Observed verbatim (Npgsql 10, DateTimeOffsetConverter.WriteCore):
            //   Cannot write DateTimeOffset with Offset=-05:00:00 to PostgreSQL type
            //   'timestamp with time zone', only offset 0 (UTC) is supported.  (Parameter 'value')
            Flatten(thrown).Should().Contain(
                "Cannot write DateTimeOffset with Offset=-05:00:00 to PostgreSQL type "
                + "'timestamp with time zone', only offset 0 (UTC) is supported.",
                "the rejection must be the converter's offset check, not an unrelated failure");

            // …and nothing was written by the rejected bind.
            var rows = await dataSource.ExecuteScalarAsync<long?>(
                "SELECT COUNT(*) FROM canned_responses WHERE tenant_id = @TenantId",
                p => p.Add(new NpgsqlParameter("TenantId", Tenant.Value)),
                CancellationToken.None) ?? 0L;

            rows.Should().Be(1);
        }
    }

    private const string InsertSql =
        "INSERT INTO canned_responses " +
        "(response_id, tenant_id, shortcut, title, body, category, tags, created_by, created_at, updated_at) " +
        "VALUES (@ResponseId, @TenantId, @Shortcut, @Title, @Body, @Category, @Tags, @CreatedBy, @CreatedAt, @UpdatedAt)";

    /// <summary>
    /// Binds one row, passing <paramref name="createdAt"/> to the <c>timestamptz</c> column as a
    /// <see cref="DateTimeOffset"/> — the bind shape used by the compiled Pro Postgres stores,
    /// as opposed to the <c>.UtcDateTime</c> that Platform's own stores pass.
    /// </summary>
    private static void BindRow(NpgsqlParameterCollection p, string responseId, DateTimeOffset createdAt)
    {
        p.Add(new NpgsqlParameter("ResponseId", responseId));
        p.Add(new NpgsqlParameter("TenantId", Tenant.Value));
        p.Add(new NpgsqlParameter("Shortcut", $"/s-{responseId}"));
        p.Add(new NpgsqlParameter("Title", "Ingress"));
        p.Add(new NpgsqlParameter("Body", "Ingress body"));
        p.Add(new NpgsqlParameter("Category", NpgsqlDbType.Text) { Value = DBNull.Value });
        p.Add(new NpgsqlParameter("Tags", NpgsqlDbType.Text) { Value = DBNull.Value });
        p.Add(new NpgsqlParameter("CreatedBy", "ingress-user"));
        p.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = createdAt });
        p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = DBNull.Value });
    }

    /// <summary>Concatenates the message of <paramref name="exception"/> and every inner one, so the
    /// assertion holds whether or not Npgsql wraps the converter's ArgumentException.</summary>
    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
