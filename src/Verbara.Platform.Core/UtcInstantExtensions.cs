namespace Verbara.Platform.Core;

/// <summary>
/// Normalises a <see cref="DateTimeOffset"/> to offset zero before it reaches a PostgreSQL
/// <c>timestamptz</c> parameter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Npgsql's modern <c>DateTimeOffsetConverter</c> <b>rejects</b> any
/// <see cref="DateTimeOffset"/> whose <see cref="DateTimeOffset.Offset"/> is not
/// <see cref="TimeSpan.Zero"/> when writing to <c>timestamp with time zone</c>, throwing
/// <c>ArgumentException: Cannot write DateTimeOffset with Offset=… to PostgreSQL type
/// 'timestamp with time zone', only offset 0 (UTC) is supported.</c> — even when the parameter
/// declares an explicit <c>NpgsqlDbType.TimestampTz</c>. The legacy converter silently called
/// <c>.UtcDateTime</c> on the caller's behalf; the modern one does not.
/// </para>
/// <para>
/// The trigger is more common than an explicitly-suffixed offset:
/// <c>DateTimeOffset.Parse("2026-08-20T10:00:00")</c> on a string carrying <b>no</b> offset stamps
/// the <b>machine-local</b> offset, so on a non-UTC host ordinary offset-less client input is
/// already non-zero. ASP.NET Core's query binder does not help — it uses
/// <c>DateTimeStyles.AssumeUniversal</c>, which only supplies a <i>missing</i> offset and leaves an
/// explicit one verbatim.
/// </para>
/// <para>
/// <b>Always convert, never relabel.</b> This is <see cref="DateTimeOffset.ToUniversalTime"/> and
/// must stay that way. <c>DateTime.SpecifyKind(x, DateTimeKind.Utc)</c> relabels without converting
/// and silently shifts the instant by the host's offset.
/// </para>
/// <para>
/// Applied at <b>ingress</b> — where an untrusted offset enters — rather than at each parameter
/// bind, because ingress is the only placement that also covers the Postgres stores inside the
/// compiled <c>Verbara.Sdk.Pro</c> packages, which this repo cannot edit.
/// </para>
/// </remarks>
public static class UtcInstantExtensions
{
    /// <summary>
    /// Returns the same instant expressed at offset zero. A no-op when the value is already UTC.
    /// </summary>
    public static DateTimeOffset ToUtcInstant(this DateTimeOffset value)
        => value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();

    /// <summary>
    /// Returns the same instant expressed at offset zero, passing <see langword="null"/> through.
    /// </summary>
    public static DateTimeOffset? ToUtcInstant(this DateTimeOffset? value)
        => value?.ToUtcInstant();
}
