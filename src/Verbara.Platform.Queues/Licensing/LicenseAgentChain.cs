using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Verbara.Platform.Queues.Licensing;

/// <summary>
/// The <c>lac1</c> hash chain of the licensed-agent ledger (licensed-agent-ledger, design D6).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Canonical form: the fields joined with <c>|</c>; a null renders as the empty string, an instant as
/// UTC round-trip (<c>O</c>, seven fractional digits and <c>Z</c>), a day as <c>yyyy-MM-dd</c>, a boolean as
/// <c>true</c>/<c>false</c>, a number in the invariant culture, the event id as a lowercase hyphenated GUID.</item>
/// <item>Ledger row: <c>prevHash|tenantId|sequence|eventId|kind|occurredAt|agentId|userId|actorUserId|conversationId|userStatus|counted|licenseId</c>.</item>
/// <item>Daily row: <c>prevHash|tenantId|sequence|day|revision|licensedAgents|closedAt|closedThroughSequence|licenseId</c>.</item>
/// <item><c>rowHash = "lac1:" + hex(SHA-256(UTF-8(canonical)))</c>, lowercase hex.</item>
/// <item>Genesis: <c>"lac1:" + hex(SHA-256("genesis|" + (tenantId ?? "deployment") + "|" + (licenseId ?? "")))</c>,
/// the <c>prevHash</c> of a chain's first row (its <c>chain_anchored</c> row). A licence change appends a
/// <c>chain_reanchored</c> row linked to the old head, never a new genesis.</item>
/// </list>
/// The chain deters rewriting; it proves nothing, because the owner controls the database (ADR §6).
/// </remarks>
public static class LicenseAgentChain
{
    /// <summary>The hash scheme prefix of every <c>prevHash</c> and <c>rowHash</c>.</summary>
    public const string Scheme = "lac1";

    /// <summary>The chain key of the deployment chain (its rows carry a null tenant id).</summary>
    public const string DeploymentChainKey = "*deployment";

    private const string Prefix = Scheme + ":";

    public static string ChainKeyOf(string? tenantId) => tenantId ?? DeploymentChainKey;

    public static string? TenantIdOf(string chainKey) =>
        string.Equals(chainKey, DeploymentChainKey, StringComparison.Ordinal) ? null : chainKey;

    /// <summary>The genesis value a chain's first row links to.</summary>
    public static string Genesis(string? tenantId, string? licenseId) =>
        Hash("genesis|" + (tenantId ?? "deployment") + "|" + (licenseId ?? ""));

    /// <summary>Whether a chain whose head carries <paramref name="headLicenseId"/> must re-anchor before the next append.</summary>
    public static bool NeedsReanchor(string? headLicenseId, string? currentLicenseId) =>
        !string.Equals(headLicenseId, currentLicenseId, StringComparison.Ordinal);

    /// <summary>
    /// The instant as stored: UTC, truncated to the microsecond Postgres <c>timestamptz</c> keeps, so a row
    /// hashed before the insert still hashes the same when read back.
    /// </summary>
    public static DateTimeOffset Normalize(DateTimeOffset instant)
    {
        var utcTicks = instant.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }

    /// <summary>The canonical rendering of an instant: UTC round-trip format.</summary>
    public static string FormatInstant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static string CanonicalEvent(LicenseAgentEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Join(
            row.PrevHash,
            row.TenantId,
            Number(row.Sequence),
            row.EventId.ToString("D", CultureInfo.InvariantCulture),
            row.Kind,
            FormatInstant(row.OccurredAt),
            row.AgentId,
            row.UserId,
            row.ActorUserId,
            row.ConversationId,
            row.UserStatus,
            Bool(row.Counted),
            row.LicenseId);
    }

    public static string CanonicalDaily(LicenseAgentDaily row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Join(
            row.PrevHash,
            row.TenantId,
            Number(row.Sequence),
            row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Number(row.Revision),
            Number(row.LicensedAgents),
            FormatInstant(row.ClosedAt),
            Number(row.ClosedThroughSequence),
            row.LicenseId);
    }

    public static string RowHash(LicenseAgentEvent row) => Hash(CanonicalEvent(row));

    public static string RowHash(LicenseAgentDaily row) => Hash(CanonicalDaily(row));

    /// <summary>The row with its <c>rowHash</c> computed from its other fields.</summary>
    public static LicenseAgentEvent Seal(LicenseAgentEvent row) => row with { RowHash = RowHash(row) };

    /// <summary>The row with its <c>rowHash</c> computed from its other fields.</summary>
    public static LicenseAgentDaily Seal(LicenseAgentDaily row) => row with { RowHash = RowHash(row) };

    /// <summary><c>"lac1:"</c> plus the lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="canonical"/>.</summary>
    public static string Hash(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), digest);
        return Prefix + Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Verifies one chain's rows (events and daily rows together, in any order): sequences are gap-free, every
    /// <c>rowHash</c> recomputes, every <c>prevHash</c> is its predecessor's <c>rowHash</c>, and a chain that
    /// starts at sequence 1 starts from the genesis bound to <paramref name="tenantId"/> and
    /// <paramref name="anchorLicenseId"/>. Returns the sequence of the first row that fails, or null.
    /// </summary>
    public static long? Verify(
        string? tenantId,
        string? anchorLicenseId,
        IEnumerable<LicenseAgentEvent> events,
        IEnumerable<LicenseAgentDaily> daily)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(daily);

        var links = events.Select(e => (e.Sequence, e.PrevHash, e.RowHash, Computed: RowHash(e)))
            .Concat(daily.Select(d => (d.Sequence, d.PrevHash, d.RowHash, Computed: RowHash(d))))
            .OrderBy(l => l.Sequence)
            .ToList();

        string? previous = null;
        long? previousSequence = null;
        foreach (var link in links)
        {
            var expectedPrev = previous ?? (link.Sequence == 1 ? Genesis(tenantId, anchorLicenseId) : link.PrevHash);
            if (previousSequence is { } p && link.Sequence != p + 1)
                return link.Sequence;
            if (!string.Equals(link.PrevHash, expectedPrev, StringComparison.Ordinal)
                || !string.Equals(link.RowHash, link.Computed, StringComparison.Ordinal))
                return link.Sequence;
            previous = link.RowHash;
            previousSequence = link.Sequence;
        }

        return null;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "",
    };

    private static string Join(params string?[] fields) => string.Join('|', fields.Select(f => f ?? ""));
}
