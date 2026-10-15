using System.Diagnostics.Metrics;
using Verbara.Platform.Identity.Mfa;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Refresh cadence of the recovery-code census behind <see cref="RecoveryCodeFamilyMetrics"/>, read from
/// <c>Mfa:RecoveryCodeCensus:InitialDelay</c> and <c>Mfa:RecoveryCodeCensus:Interval</c> (TimeSpan strings).
/// </summary>
/// <param name="InitialDelay">Wait before the first census (default 2 minutes), so it never runs during boot.</param>
/// <param name="Interval">Wait between censuses (default 60 minutes). <see cref="TimeSpan.Zero"/> disables the census.</param>
internal sealed record RecoveryCodeCensusOptions(TimeSpan InitialDelay, TimeSpan Interval)
{
    public static RecoveryCodeCensusOptions Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(60));

    /// <summary>Read the two keys, falling back to <see cref="Default"/> for a missing or unparsable value.</summary>
    public static RecoveryCodeCensusOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Mfa:RecoveryCodeCensus");
        return new RecoveryCodeCensusOptions(
            Read(section["InitialDelay"], Default.InitialDelay),
            Read(section["Interval"], Default.Interval));
    }

    private static TimeSpan Read(string? value, TimeSpan fallback) =>
        TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= TimeSpan.Zero
            ? parsed
            : fallback;
}

/// <summary>
/// The recovery-code family meter <c>Verbara.Platform.Auth.RecoveryCodes</c>: how many users are still on a
/// legacy digest family, and which family each redemption used.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>mfa.recovery_codes.users{family}</c></b> (gauge, <c>family</c> = <c>argon2id</c> | <c>legacy</c>)
/// reports the last successful <see cref="IRecoveryCodeCensus"/>. Recovery codes do not converge on their
/// own (only a regenerate moves a user to Argon2id), so the legacy series is what an operator watches to
/// see whether users are regenerating. Until the first census succeeds the gauge reports nothing; a failed
/// census is logged and the previous value kept.
/// </para>
/// <para>
/// <b><c>mfa.recovery_codes.redeemed{family}</c></b> (counter, <c>family</c> = <c>argon2id</c> | <c>bcrypt</c>
/// | <c>sha256</c>) is incremented by <c>MfaVerify</c> only after the matched code was consumed, so it counts
/// redemptions, not attempts.
/// </para>
/// <para>
/// Neither instrument carries a tenant or user tag: <c>/metrics</c> is unauthenticated.
/// </para>
/// <para>
/// The census seam is resolved at the first tick, never in <see cref="BackgroundService.StartAsync"/>: a
/// host that boots without a database (design-time tools, headless dispatch) must not crash on it. The
/// census is a full unwrapping scan of <c>users</c>, so it runs on a slow timer
/// (<see cref="RecoveryCodeCensusOptions"/>).
/// </para>
/// </remarks>
internal sealed partial class RecoveryCodeFamilyMetrics : BackgroundService
{
    /// <summary>Public meter name (consumed by the OpenTelemetry registration).</summary>
    public const string MeterName = "Verbara.Platform.Auth.RecoveryCodes";

    private readonly IServiceProvider _services;
    private readonly RecoveryCodeCensusOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RecoveryCodeFamilyMetrics> _logger;
    private readonly Counter<long> _redeemed;

    // The last census, published whole; null until the first one succeeds.
    private RecoveryCodeCensus? _last;

    public RecoveryCodeFamilyMetrics(
        IServiceProvider services,
        IMeterFactory meterFactory,
        RecoveryCodeCensusOptions options,
        TimeProvider time,
        ILogger<RecoveryCodeFamilyMetrics> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _options = options;
        _time = time;
        _logger = logger;

        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge(
            "mfa.recovery_codes.users",
            Observe,
            unit: "{user}",
            description: "Users holding recovery codes, by digest family (argon2id or legacy), from the last census.");
        _redeemed = meter.CreateCounter<long>(
            "mfa.recovery_codes.redeemed",
            unit: "{code}",
            description: "Recovery codes redeemed at POST /auth/mfa/verify, by the digest family they were stored in.");
    }

    /// <summary>Count one consumed recovery code under its stored family.</summary>
    public void RecordRedeemed(RecoveryCodeFamily family) =>
        _redeemed.Add(1, new KeyValuePair<string, object?>("family", family switch
        {
            RecoveryCodeFamily.Argon2id => "argon2id",
            RecoveryCodeFamily.Bcrypt => "bcrypt",
            _ => "sha256",
        }));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Interval <= TimeSpan.Zero)
            return;

        try
        {
            await Task.Delay(_options.InitialDelay, _time, stoppingToken);

            IRecoveryCodeCensus? census = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Resolved here, at first use, never at host start (deferred-seam rule).
                    census ??= _services.GetRequiredService<IRecoveryCodeCensus>();
                    Volatile.Write(ref _last, await census.CountAsync(stoppingToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Keep the previous value; the next tick retries.
                    LogCensusFailed(_logger, ex);
                }

                await Task.Delay(_options.Interval, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private IEnumerable<Measurement<long>> Observe()
    {
        if (Volatile.Read(ref _last) is not { } last)
            return [];

        return
        [
            new Measurement<long>(last.UsersOnArgon2id, new KeyValuePair<string, object?>("family", "argon2id")),
            new Measurement<long>(last.UsersOnLegacy, new KeyValuePair<string, object?>("family", "legacy")),
        ];
    }

    [LoggerMessage(EventId = 4351, Level = LogLevel.Warning,
        Message = "Recovery-code census failed; the gauge keeps its previous value and the next tick retries")]
    private static partial void LogCensusFailed(ILogger logger, Exception ex);
}
